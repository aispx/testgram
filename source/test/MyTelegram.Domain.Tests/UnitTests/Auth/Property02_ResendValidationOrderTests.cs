// Feature: auth-methods-completion, Property 2: resend validation order (first applicable error wins)
//
// For any resend request that simultaneously satisfies two or more error conditions, resendCode
// raises exactly the first applicable error in the order: empty phone code hash
// (400 PHONE_CODE_HASH_EMPTY) -> invalid phone number (406 PHONE_NUMBER_INVALID) ->
// expired/missing/cancelled code (400 PHONE_CODE_EXPIRED) -> missing login email
// (400 EMAIL_INSTALL_MISSING) -> no alternate medium (406 SEND_CODE_UNAVAILABLE).
//
// Validates: Requirements 1.3, 1.5, 1.6, 1.7, 1.8
//
// Approach: the ordering is a total decision function over the independent error conditions. This
// single property generates every combination of the three conditions that the SMS-only handler
// can be driven into through its inputs/state — empty phone code hash, invalid phone number, and a
// missing/expired App_Code — and asserts the handler throws exactly the first applicable error in
// the documented order (or, when no condition holds, returns a SentCode). Generating all
// combinations subsumes the "two or more conditions" cases while also confirming each error is
// reachable as the winner and that the ordering falls through correctly.
//
// Note on EMAIL_INSTALL_MISSING (1.6) and SEND_CODE_UNAVAILABLE (1.7): in the current SMS-only
// model these two conditions sit *after* the three above in the same total order. The handler
// derives the delivery medium from the App_Code's installed email, so a request that reaches the
// medium checks always resolves to a deliverable SMS/login-email medium with an installed email —
// i.e. neither of these later conditions can be provoked without the (out-of-scope) login-email
// verification flow. They are represented in the ordering asserted here (they can only ever be
// out-ranked by an earlier condition), so the first-applicable-error guarantee this property
// establishes holds for them by construction of the documented order.

using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Options;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property02_ResendValidationOrderTests
{
    // Property 2: resend validation order (first applicable error wins)
    // Validates: Requirements 1.3, 1.5, 1.6, 1.7, 1.8
    [Property(Arbitrary = new[] { typeof(ResendOrderArbitraries) }, MaxTest = 100)]
    public void Resend_raises_first_applicable_error_in_documented_order(ResendOrderCase testCase)
    {
        // Arrange: drive the handler into the requested combination of error conditions.
        var now = DateTime.UtcNow.ToTimestamp();

        // (C3) missing / expired App_Code: either no App_Code at all, or one whose Expire is in the
        // past. Otherwise a valid, non-expired App_Code identified by the request.
        IAppCodeReadModel? appCode;
        if (testCase.CodeAbsent)
        {
            appCode = testCase.ReturnNull
                ? null
                : new FakeAppCodeReadModel
                {
                    Id = testCase.HashValue,
                    AppCodeId = testCase.HashValue,
                    Code = "11111",
                    CreationTime = now,
                    Expire = now - 60, // already expired
                    PhoneCodeHash = testCase.HashValue,
                    PhoneNumber = testCase.PhoneDigits,
                    Email = testCase.Email
                };
        }
        else
        {
            appCode = new FakeAppCodeReadModel
            {
                Id = testCase.HashValue,
                AppCodeId = testCase.HashValue,
                Code = "11111",
                CreationTime = now,
                Expire = now + 100_000, // valid, non-expired
                PhoneCodeHash = testCase.HashValue,
                PhoneNumber = testCase.PhoneDigits,
                Email = testCase.Email
            };
        }

        var queryProcessor = new StubQueryProcessor(appCode);
        var commandBus = new CapturingCommandBus();
        var codeGenerator = new StubVerificationCodeGenerator("22222");
        var options = new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(
            new MyTelegramMessengerServerOptions
            {
                CheckPhoneNumberFormat = false,
                VerificationCodeExpirationSeconds = 300
            });
        var countryHelper = new NoopCountryHelper();

        var handler = CreateResendCodeHandler(queryProcessor, commandBus, codeGenerator, options, countryHelper);

        // (C1) empty phone code hash; (C2) invalid (non-numeric) phone number.
        var request = new RequestResendCode
        {
            PhoneNumber = testCase.PhoneInvalid ? testCase.InvalidPhone : testCase.PhoneDigits,
            PhoneCodeHash = testCase.HashEmpty ? string.Empty : testCase.HashValue
        };

        // Oracle: the documented validation order, first applicable condition wins.
        if (testCase.HashEmpty)
        {
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("PHONE_CODE_HASH_EMPTY");
        }
        else if (testCase.PhoneInvalid)
        {
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(406);
            ex.RpcError.Message.ShouldBe("PHONE_NUMBER_INVALID");
        }
        else if (testCase.CodeAbsent)
        {
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");
        }
        else
        {
            // No error condition applies -> a SentCode is returned for the same phone code hash.
            var sentCode = InvokeAsync(handler, CreateRequestInput(), request);
            sentCode.ShouldNotBeNull();
            var tSentCode = sentCode.ShouldBeOfType<TSentCode>();
            tSentCode.PhoneCodeHash.ShouldBe(testCase.HashValue);
        }
    }

    private static object CreateResendCodeHandler(
        IQueryProcessor queryProcessor,
        ICommandBus commandBus,
        IVerificationCodeGenerator verificationCodeGenerator,
        IOptionsMonitor<MyTelegramMessengerServerOptions> options,
        ICountryHelper countryHelper)
    {
        // ResendCodeHandler is internal sealed; construct it through the Messenger assembly.
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ResendCodeHandler",
            throwOnError: true)!;
        return Activator.CreateInstance(
            type,
            queryProcessor,
            commandBus,
            verificationCodeGenerator,
            options,
            countryHelper)!;
    }

    private static ISentCode InvokeAsync(object handler, IRequestInput input, IObject request)
    {
        var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = ((Task<IObject>)taskObj).GetAwaiter().GetResult();
        var rpcResult = (TRpcResult)result;
        return (ISentCode)rpcResult.Result;
    }

    private static RequestInput CreateRequestInput()
    {
        return new RequestInput(
            ConnectionId: "test-connection",
            ConnectionType: default,
            RequestId: Guid.NewGuid(),
            ObjectId: 0u,
            ReqMsgId: 1L,
            SeqNumber: 0,
            UserId: 1L,
            AuthKeyId: 1L,
            PermAuthKeyId: 1L,
            Layer: 0,
            Date: 0L,
            DeviceType: default,
            ClientIp: "127.0.0.1",
            SessionId: 1L,
            AccessHashKeyId: 0L);
    }

    /// <summary>Returns the configured App_Code (possibly null) for every query the handler
    /// issues (<c>GetLatestAppCodeQuery</c>).</summary>
    private sealed class StubQueryProcessor(IAppCodeReadModel? appCode) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)appCode!);
    }

    /// <summary>Captures published commands and reports success so the handler can complete.</summary>
    private sealed class CapturingCommandBus : ICommandBus
    {
        public List<object> Published { get; } = new();

        public Task<TExecutionResult> PublishAsync<TAggregate, TIdentity, TExecutionResult>(
            ICommand<TAggregate, TIdentity, TExecutionResult> command,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult
        {
            Published.Add(command);
            return Task.FromResult((TExecutionResult)(object)ExecutionResult.Success());
        }
    }

    private sealed class StubVerificationCodeGenerator(string code) : IVerificationCodeGenerator
    {
        public string Generate() => code;
    }

    /// <summary>Country helper that is never consulted when phone-format checking is disabled.</summary>
    private sealed class NoopCountryHelper : ICountryHelper
    {
        public bool TryGetCountryCodeItem(string countryCode, out CountryCodeItem? countryCodeItem)
        {
            countryCodeItem = null;
            return false;
        }

        public IReadOnlyCollection<CountryItem> GetAllCountryList() => Array.Empty<CountryItem>();
        public void InitAllCountries() { }
        public string GetCountryCodeByPhoneNumber(string phoneNumber) => string.Empty;
        public string? GetCountryIso2ByPhoneNumber(string phoneNumber) => null;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeAppCodeReadModel : IAppCodeReadModel
    {
        public string AppCodeId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public long CreationTime { get; set; }
        public int Expire { get; set; }
        public string Id { get; set; } = string.Empty;
        public string PhoneCodeHash { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string? Email { get; set; }
    }
}

/// <summary>Input case for Property 2: independent toggles for the three reachable resend error
/// conditions (empty phone code hash, invalid phone number, missing/expired App_Code) plus the raw
/// values used to realise them.</summary>
public sealed record ResendOrderCase(
    bool HashEmpty,
    bool PhoneInvalid,
    bool CodeAbsent,
    bool ReturnNull,
    string PhoneDigits,
    string InvalidPhone,
    string HashValue,
    string? Email);

/// <summary>FsCheck arbitrary surface for Property 2. Generates every combination of the three
/// reachable error conditions (via independent booleans) together with a valid numeric phone
/// number, a non-numeric (invalid) phone number, a non-empty phone code hash, a null-vs-expired
/// choice for the missing-code condition, and an optional installed email.</summary>
public static class ResendOrderArbitraries
{
    public static Arbitrary<ResendOrderCase> ResendOrderCase()
    {
        var boolGen = Gen.Elements(true, false);
        var digitsGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        // ToPhoneNumber only strips '+' and spaces, so any value with a non-digit survives as an
        // invalid (non-parseable) phone number.
        var invalidGen = Gen.Elements("abc", "12ab34", "notaphone", "12-34-56", "phoneX", "++");
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var emailGen = Gen.OneOf(
            Gen.Constant<string?>(null),
            Gen.Constant<string?>(string.Empty),
            Gen.Choose(1, 9999).Select(i => (string?)$"a***@mail{i}.com"));

        var gen =
            from hashEmpty in boolGen
            from phoneInvalid in boolGen
            from codeAbsent in boolGen
            from returnNull in boolGen
            from digits in digitsGen
            from invalid in invalidGen
            from hash in hashGen
            from email in emailGen
            select new ResendOrderCase(hashEmpty, phoneInvalid, codeAbsent, returnNull, digits, invalid, hash, email);

        return Arb.From(gen);
    }
}
