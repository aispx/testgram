// Feature: auth-methods-completion, Property 1: Valid resend returns a SentCode for the recorded medium
//
// For any pending App_Code that is non-expired and non-cancelled, and a resend request with the
// same phone number and Phone_Code_Hash, resendCode returns a non-null ISentCode whose
// PhoneCodeHash equals the request hash and whose Type corresponds to the App_Code's recorded
// delivery medium.
//
// Validates: Requirements 1.1, 1.2
//
// The property drives the production (internal) ResendCodeHandler via reflection (mirroring the
// approach used by the Messenger handler tests in MyTelegram.Services.Tests) with hand-rolled
// fakes: a query processor returning the generated App_Code, a capturing command bus, a fixed
// verification-code generator, a static options monitor (phone-format check disabled so any
// numeric phone number is accepted), and an unused country helper. It asserts the returned
// SentCode carries the request's PhoneCodeHash and a Type matching the recorded medium (an
// App_Code with an installed email was a login-email delivery -> TSentCodeTypeEmailCode; otherwise
// SMS -> TSentCodeTypeSms).

using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using EventFlow.ReadStores;
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

public class Property01_ValidResendTests
{
    // Property 1: Valid resend returns a SentCode for the recorded medium
    // Validates: Requirements 1.1, 1.2
    [Property(Arbitrary = new[] { typeof(ResendArbitraries) }, MaxTest = 100)]
    public void Valid_resend_returns_sent_code_for_recorded_medium(ValidResendCase testCase)
    {
        // Arrange: a non-expired, non-cancelled App_Code identified by the request's phone number
        // and phone code hash. Expire is set well ahead of "now" so the code is always valid.
        var now = DateTime.UtcNow.ToTimestamp();
        var appCode = new FakeAppCodeReadModel
        {
            Id = testCase.PhoneCodeHash,
            AppCodeId = testCase.PhoneCodeHash,
            Code = "11111",
            CreationTime = now,
            Expire = now + testCase.ExpireOffsetSeconds,
            PhoneCodeHash = testCase.PhoneCodeHash,
            PhoneNumber = testCase.PhoneNumber,
            Email = testCase.Email
        };

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
        var request = new RequestResendCode
        {
            PhoneNumber = testCase.PhoneNumber,
            PhoneCodeHash = testCase.PhoneCodeHash
        };

        // Act
        var sentCode = InvokeAsync(handler, CreateRequestInput(), request);

        // Assert: a SentCode for the same phone code hash, whose Type matches the recorded medium.
        sentCode.ShouldNotBeNull();
        var tSentCode = sentCode.ShouldBeOfType<TSentCode>();
        tSentCode.PhoneCodeHash.ShouldBe(testCase.PhoneCodeHash);

        if (string.IsNullOrEmpty(testCase.Email))
        {
            // No installed email -> the prior delivery was an SMS code (Requirement 1.2).
            tSentCode.Type.ShouldBeOfType<TSentCodeTypeSms>();
        }
        else
        {
            // An installed login email -> the recorded medium is a login-email code.
            tSentCode.Type.ShouldBeOfType<TSentCodeTypeEmailCode>();
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

    /// <summary>Returns the configured App_Code for every query the handler issues
    /// (<c>GetLatestAppCodeQuery</c>).</summary>
    private sealed class StubQueryProcessor(IAppCodeReadModel appCode) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)appCode);
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

/// <summary>Input case for Property 1: a valid, non-expired resend request together with the
/// App_Code's installed email (null/empty models an SMS delivery, a non-empty pattern models a
/// login-email delivery).</summary>
public sealed record ValidResendCase(string PhoneNumber, string PhoneCodeHash, string? Email, int ExpireOffsetSeconds);

/// <summary>FsCheck arbitrary surface for Property 1. Generates numeric phone numbers (accepted
/// when phone-format checking is disabled), non-empty phone code hashes, an email that is either
/// absent (SMS medium) or a pattern (login-email medium), and a positive expiry offset so the
/// App_Code is always non-expired.</summary>
public static class ResendArbitraries
{
    public static Arbitrary<ValidResendCase> ValidResendCase()
    {
        var phoneGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var emailGen = Gen.OneOf(
            Gen.Constant<string?>(null),
            Gen.Constant<string?>(string.Empty),
            Gen.Choose(1, 9999).Select(i => (string?)$"a***@mail{i}.com"));
        var expireGen = Gen.Choose(60, 1_000_000);

        var gen =
            from phone in phoneGen
            from hash in hashGen
            from email in emailGen
            from expire in expireGen
            select new ValidResendCase(phone, hash, email, expire);

        return Arb.From(gen);
    }
}
