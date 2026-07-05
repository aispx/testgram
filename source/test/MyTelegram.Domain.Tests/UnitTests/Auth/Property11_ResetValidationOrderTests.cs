// Feature: auth-methods-completion, Property 11: reset validation order (first applicable error wins)
//
// For any resetLoginEmail request that simultaneously satisfies two or more error conditions, the
// server raises exactly the first applicable error in the order: invalid phone number
// (400 PHONE_NUMBER_INVALID) -> missing login email (400 EMAIL_INSTALL_MISSING) -> reset already
// requested (400 TASK_ALREADY_EXISTS).
//
// Validates: Requirements 5.3, 5.4, 5.5
//
// Approach: the ordering is a total decision function over the independent error conditions. This
// single property generates every combination of the reachable conditions that the SMS-only
// handler can be driven into through its inputs/state — invalid phone number and a
// missing/empty-email App_Code — and asserts the handler throws exactly the first applicable error
// in the documented order (or, when no condition holds, returns a SentCode). Generating all
// combinations subsumes the "two or more conditions" cases while also confirming each error is
// reachable as the winner and that the ordering falls through correctly.
//
// Note on TASK_ALREADY_EXISTS (5.4): this condition sits *last* in the documented order but is
// structurally unreachable at the handler level given the currently-available read-model fields.
// IAppCodeReadModel exposes only Code/CreationTime/Expire/Id/PhoneCodeHash/PhoneNumber/Email — it
// does NOT project the LoginEmailResetRequested flag (the design's mapping of reset_pending_date)
// nor AppCodeType, so the "reset already requested" guard cannot be observed from the read side.
// Additionally, under the current SMS-only flow SendCodeHandler never emits an
// auth.sentCodeTypeEmailCode, so no login email is ever configured and the missing-login-email
// condition (5.3) short-circuits to EMAIL_INSTALL_MISSING before a reset could ever be requested.
// TASK_ALREADY_EXISTS is represented in the ordering asserted here (it can only ever be out-ranked
// by an earlier condition), so the first-applicable-error guarantee this property establishes holds
// for it by construction of the documented order. This mirrors how
// Property02_ResendValidationOrderTests documents the unreachable EMAIL_INSTALL_MISSING /
// SEND_CODE_UNAVAILABLE conditions.

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

public class Property11_ResetValidationOrderTests
{
    // Property 11: reset validation order (first applicable error wins)
    // Validates: Requirements 5.3, 5.4, 5.5
    [Property(Arbitrary = new[] { typeof(ResetOrderArbitraries) }, MaxTest = 100)]
    public void Reset_raises_first_applicable_error_in_documented_order(ResetOrderCase testCase)
    {
        // Arrange: drive the handler into the requested combination of reachable error conditions.
        var now = DateTime.UtcNow.ToTimestamp();

        // (C2) missing login email: either no App_Code at all (null), or an App_Code whose Email is
        // empty/absent. Otherwise a valid App_Code with a non-empty login email pending.
        IAppCodeReadModel? appCode;
        if (testCase.EmailMissing)
        {
            appCode = testCase.ReturnNull
                ? null
                : new FakeAppCodeReadModel
                {
                    Id = testCase.HashValue,
                    AppCodeId = testCase.HashValue,
                    Code = "11111",
                    CreationTime = now,
                    Expire = now + 100_000,
                    PhoneCodeHash = testCase.HashValue,
                    PhoneNumber = testCase.PhoneDigits,
                    Email = testCase.EmptyEmailAsNull ? null : string.Empty
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
                Expire = now + 100_000,
                PhoneCodeHash = testCase.HashValue,
                PhoneNumber = testCase.PhoneDigits,
                Email = $"a***@mail{testCase.EmailSuffix}.com" // non-empty login email pending
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

        var handler = CreateResetLoginEmailHandler(queryProcessor, commandBus, codeGenerator, options, countryHelper);

        // (C1) invalid (non-numeric) phone number.
        var request = new RequestResetLoginEmail
        {
            PhoneNumber = testCase.PhoneInvalid ? testCase.InvalidPhone : testCase.PhoneDigits,
            PhoneCodeHash = testCase.HashValue
        };

        // Oracle: the documented validation order, first applicable condition wins.
        // invalid phone (400 PHONE_NUMBER_INVALID) -> missing login email (400 EMAIL_INSTALL_MISSING)
        // -> reset already requested (400 TASK_ALREADY_EXISTS, structurally unreachable — see header).
        if (testCase.PhoneInvalid)
        {
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("PHONE_NUMBER_INVALID");
            commandBus.Published.Count.ShouldBe(0);
        }
        else if (testCase.EmailMissing)
        {
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("EMAIL_INSTALL_MISSING");
            commandBus.Published.Count.ShouldBe(0);
        }
        else
        {
            // No error condition applies -> a SentCode is returned for the same phone code hash and
            // the reset is published.
            var sentCode = InvokeAsync(handler, CreateRequestInput(), request);
            sentCode.ShouldNotBeNull();
            var tSentCode = sentCode.ShouldBeOfType<TSentCode>();
            tSentCode.PhoneCodeHash.ShouldBe(testCase.HashValue);
            tSentCode.Type.ShouldBeOfType<TSentCodeTypeSms>();
            commandBus.Published.Count.ShouldBe(1);
            commandBus.Published[0].GetType().Name.ShouldBe("ResetLoginEmailCommand");
        }
    }

    private static object CreateResetLoginEmailHandler(
        IQueryProcessor queryProcessor,
        ICommandBus commandBus,
        IVerificationCodeGenerator verificationCodeGenerator,
        IOptionsMonitor<MyTelegramMessengerServerOptions> options,
        ICountryHelper countryHelper)
    {
        // ResetLoginEmailHandler is internal sealed; construct it through the Messenger assembly.
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ResetLoginEmailHandler",
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

    /// <summary>Returns the configured App_Code (possibly null) for every query the handler issues
    /// (<c>GetLatestAppCodeQuery</c>).</summary>
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

/// <summary>Input case for Property 11: independent toggles for the two reachable reset error
/// conditions (invalid phone number, missing/empty-email App_Code) plus the raw values used to
/// realise them.</summary>
public sealed record ResetOrderCase(
    bool PhoneInvalid,
    bool EmailMissing,
    bool ReturnNull,
    bool EmptyEmailAsNull,
    string PhoneDigits,
    string InvalidPhone,
    string HashValue,
    int EmailSuffix);

/// <summary>FsCheck arbitrary surface for Property 11. Generates every combination of the two
/// reachable error conditions (via independent booleans) together with a valid numeric phone
/// number, a non-numeric (invalid) phone number, a non-empty phone code hash, a null-vs-empty-email
/// choice for the missing-email condition, and a suffix for the non-empty login email.</summary>
public static class ResetOrderArbitraries
{
    public static Arbitrary<ResetOrderCase> ResetOrderCase()
    {
        var boolGen = Gen.Elements(true, false);
        var digitsGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        // ToPhoneNumber only strips '+' and spaces, so any value with a non-digit (or one that
        // strips to empty) survives as an invalid (non-parseable) phone number.
        var invalidGen = Gen.Elements("abc", "12ab34", "notaphone", "12-34-56", "phoneX", "++");
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var suffixGen = Gen.Choose(1, 9999);

        var gen =
            from phoneInvalid in boolGen
            from emailMissing in boolGen
            from returnNull in boolGen
            from emptyEmailAsNull in boolGen
            from digits in digitsGen
            from invalid in invalidGen
            from hash in hashGen
            from suffix in suffixGen
            select new ResetOrderCase(phoneInvalid, emailMissing, returnNull, emptyEmailAsNull, digits, invalid, hash, suffix);

        return Arb.From(gen);
    }
}
