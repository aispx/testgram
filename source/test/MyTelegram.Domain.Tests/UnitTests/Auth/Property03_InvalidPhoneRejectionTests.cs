// Feature: auth-methods-completion, Property 3: Invalid phone number is rejected (all phone-bearing methods)
//
// For any phone number that fails phone-number validation (with a non-empty phone code hash where
// applicable), resendCode and cancelCode raise 406 PHONE_NUMBER_INVALID, and resetLoginEmail raises
// 400 PHONE_NUMBER_INVALID, changing no state (no command is published). The checkPaidAuth (7.2)
// portion is deferred alongside the deferred handler (task 10).
//
// Validates: Requirements 1.4, 2.3, 5.2
//
// Approach: the invalid-phone rejection is a shared precondition across every phone-bearing auth
// method, so this single parametric property generates, for each in-scope login-flow method
// (resendCode, cancelCode, resetLoginEmail), an invalid (non-numeric) phone number and a non-empty
// phone code hash, drives the production (internal) handler via reflection (mirroring Property
// 1/2/4/10) with hand-rolled fakes (a query processor and a capturing command bus), and asserts the
// documented RpcError for that method is raised AND no command was published (no state change).
//
// Extensibility: the method → (constructor factory, expected error) mapping lives in
// PhoneBearingMethodInfo. Task 7.4 extended Property 3 by adding a ResetLoginEmail entry (expected
// 400 PHONE_NUMBER_INVALID) to PhoneBearingMethods.Get and the arbitrary; the test body is unchanged.

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

public class Property03_InvalidPhoneRejectionTests
{
    // Property 3: Invalid phone number is rejected (all phone-bearing methods)
    // Validates: Requirements 1.4, 2.3, 5.2
    [Property(Arbitrary = new[] { typeof(InvalidPhoneArbitraries) }, MaxTest = 100)]
    public void Invalid_phone_number_is_rejected_changing_no_state(InvalidPhoneCase testCase)
    {
        // Arrange: a capturing command bus proves no state mutation is attempted, and a query
        // processor that would return a valid App_Code if consulted (so the ONLY reason to reject
        // is the invalid phone number, not a missing/expired code that is checked later).
        var now = DateTime.UtcNow.ToTimestamp();
        var appCode = new FakeAppCodeReadModel
        {
            Id = testCase.PhoneCodeHash,
            AppCodeId = testCase.PhoneCodeHash,
            Code = "11111",
            CreationTime = now,
            Expire = now + 100_000, // valid, non-expired
            PhoneCodeHash = testCase.PhoneCodeHash,
            PhoneNumber = testCase.InvalidPhone
        };
        var queryProcessor = new StubQueryProcessor(appCode);
        var commandBus = new CapturingCommandBus();

        var info = PhoneBearingMethods.Get(testCase.Method);
        var handler = info.CreateHandler(queryProcessor, commandBus);
        var request = info.CreateRequest(testCase.InvalidPhone, testCase.PhoneCodeHash);

        // Act + Assert: the documented RpcError for the method is raised, and no command was
        // published (Requirements 1.4 / 2.3 — invalid phone rejected, changing no state).
        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
        ex.RpcError.ErrorCode.ShouldBe(info.ExpectedErrorCode);
        ex.RpcError.Message.ShouldBe("PHONE_NUMBER_INVALID");

        commandBus.Published.Count.ShouldBe(0);
    }

    private static IObject InvokeAsync(object handler, IRequestInput input, IObject request)
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

        return ((Task<IObject>)taskObj).GetAwaiter().GetResult();
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

    /// <summary>Returns the configured App_Code for every query the handler issues; a valid code so
    /// that the only rejection cause is the invalid phone number.</summary>
    private sealed class StubQueryProcessor(IAppCodeReadModel? appCode) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)appCode!);
    }

    /// <summary>Captures published commands so the test can assert none were published.</summary>
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

    /// <summary>Builds a phone-format-agnostic handler (CheckPhoneNumberFormat = false) for the
    /// SentCode-returning handlers so the ONLY validation exercised is the numeric phone check.</summary>
    private static IOptionsMonitor<MyTelegramMessengerServerOptions> DefaultOptions()
        => new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(
            new MyTelegramMessengerServerOptions
            {
                CheckPhoneNumberFormat = false,
                VerificationCodeExpirationSeconds = 300
            });

    /// <summary>Reflectively constructs an internal sealed handler from the Messenger assembly.</summary>
    private static object CreateMessengerHandler(string typeName, params object[] args)
    {
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(type, args)!;
    }

    /// <summary>Per-method wiring: how to construct the handler, how to build its request, and the
    /// documented error code raised for an invalid phone number. Extended in task 7.4 to add
    /// resetLoginEmail (400).</summary>
    internal sealed record PhoneBearingMethodInfo(
        int ExpectedErrorCode,
        Func<IQueryProcessor, ICommandBus, object> CreateHandler,
        Func<string, string, IObject> CreateRequest);

    internal static class PhoneBearingMethods
    {
        public static PhoneBearingMethodInfo Get(PhoneBearingMethod method) => method switch
        {
            // resendCode: 406 PHONE_NUMBER_INVALID (Requirement 1.4).
            PhoneBearingMethod.ResendCode => new PhoneBearingMethodInfo(
                ExpectedErrorCode: 406,
                CreateHandler: (qp, cb) => CreateMessengerHandler(
                    "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ResendCodeHandler",
                    qp,
                    cb,
                    new StubVerificationCodeGenerator("22222"),
                    DefaultOptions(),
                    new NoopCountryHelper()),
                CreateRequest: (phone, hash) => new RequestResendCode
                {
                    PhoneNumber = phone,
                    PhoneCodeHash = hash
                }),

            // cancelCode: 406 PHONE_NUMBER_INVALID (Requirement 2.3).
            PhoneBearingMethod.CancelCode => new PhoneBearingMethodInfo(
                ExpectedErrorCode: 406,
                CreateHandler: (qp, cb) => CreateMessengerHandler(
                    "MyTelegram.Messenger.Handlers.LatestLayer.Auth.CancelCodeHandler",
                    qp,
                    cb),
                CreateRequest: (phone, hash) => new RequestCancelCode
                {
                    PhoneNumber = phone,
                    PhoneCodeHash = hash
                }),

            // resetLoginEmail: 400 PHONE_NUMBER_INVALID (Requirement 5.2) — note the 400 code, which
            // differs from resend/cancel's 406, per the auth.resetLoginEmail method page.
            PhoneBearingMethod.ResetLoginEmail => new PhoneBearingMethodInfo(
                ExpectedErrorCode: 400,
                CreateHandler: (qp, cb) => CreateMessengerHandler(
                    "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ResetLoginEmailHandler",
                    qp,
                    cb,
                    new StubVerificationCodeGenerator("22222"),
                    DefaultOptions(),
                    new NoopCountryHelper()),
                CreateRequest: (phone, hash) => new RequestResetLoginEmail
                {
                    PhoneNumber = phone,
                    PhoneCodeHash = hash
                }),

            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
        };
    }
}

/// <summary>The phone-bearing login-flow methods in scope. Extended in task 7.4 with
/// ResetLoginEmail (which raises 400 PHONE_NUMBER_INVALID, unlike resend/cancel's 406).</summary>
public enum PhoneBearingMethod
{
    ResendCode,
    CancelCode,
    ResetLoginEmail
}

/// <summary>Input case for Property 3: the method under test, an invalid (non-numeric) phone number
/// that fails validation, and a non-empty phone code hash (required by resendCode's earlier
/// empty-hash check so the invalid-phone condition is the one exercised).</summary>
public sealed record InvalidPhoneCase(PhoneBearingMethod Method, string InvalidPhone, string PhoneCodeHash);

/// <summary>FsCheck arbitrary surface for Property 3. Generates every in-scope method paired with a
/// non-numeric phone number (ToPhoneNumber only strips '+' and spaces, so any value with another
/// non-digit — or one that strips to empty — is invalid) and a non-empty phone code hash.</summary>
public static class InvalidPhoneArbitraries
{
    public static Arbitrary<InvalidPhoneCase> InvalidPhoneCase()
    {
        var methodGen = Gen.Elements(
            PhoneBearingMethod.ResendCode,
            PhoneBearingMethod.CancelCode,
            PhoneBearingMethod.ResetLoginEmail);
        var invalidGen = Gen.Elements("abc", "12ab34", "notaphone", "12-34-56", "phoneX", "++", "");
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);

        var gen =
            from method in methodGen
            from invalid in invalidGen
            from hash in hashGen
            select new InvalidPhoneCase(method, invalid, hash);

        return Arb.From(gen);
    }
}
