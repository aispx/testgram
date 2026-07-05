// Feature: auth-methods-completion — representative unit-test examples for auth.resendCode.
//
// These example-based ([Fact]) tests complement the Property 1/2 tests by pinning down concrete,
// documented scenarios for ResendCodeHandler:
//   - Happy path: a valid, non-expired App_Code -> a TSentCode for the request hash (Requirement 1.1).
//   - Empty phone code hash -> 400 PHONE_CODE_HASH_EMPTY (Requirement 1.3).
//   - Expired / missing App_Code -> 400 PHONE_CODE_EXPIRED (Requirement 1.5).
//
// The handler (internal sealed) is constructed through the Messenger assembly via reflection and
// invoked through its public HandleAsync, mirroring Property01_ValidResendTests /
// Property02_ResendValidationOrderTests, and reusing the same hand-rolled fakes.

using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using Microsoft.Extensions.Options;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class ResendCodeHandlerUnitTests
{
    private const string PhoneNumber = "12025550123";
    private const string PhoneCodeHash = "hash-abc123";

    // Requirement 1.1: a valid, non-expired, non-cancelled App_Code resends and returns a SentCode
    // (SMS medium, since no login email is installed) carrying the request's phone code hash.
    [Fact]
    public void Happy_path_valid_resend_returns_sent_code()
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var appCode = new FakeAppCodeReadModel
        {
            Id = PhoneCodeHash,
            AppCodeId = PhoneCodeHash,
            Code = "11111",
            CreationTime = now,
            Expire = now + 300, // valid, non-expired
            PhoneCodeHash = PhoneCodeHash,
            PhoneNumber = PhoneNumber,
            Email = null // no installed email -> SMS delivery
        };

        var commandBus = new CapturingCommandBus();
        var handler = CreateHandler(new StubQueryProcessor(appCode), commandBus);

        var request = new RequestResendCode
        {
            PhoneNumber = PhoneNumber,
            PhoneCodeHash = PhoneCodeHash
        };

        var sentCode = InvokeAsync(handler, CreateRequestInput(), request);

        sentCode.ShouldNotBeNull();
        var tSentCode = sentCode.ShouldBeOfType<TSentCode>();
        tSentCode.PhoneCodeHash.ShouldBe(PhoneCodeHash);
        tSentCode.Type.ShouldBeOfType<TSentCodeTypeSms>();
        tSentCode.Timeout.ShouldBe(300);
        // A resend must be published through the command bus (no direct state mutation).
        commandBus.Published.Count.ShouldBe(1);
    }

    // Requirement 1.3: an empty phone code hash is rejected with 400 PHONE_CODE_HASH_EMPTY.
    [Fact]
    public void Empty_phone_code_hash_raises_phone_code_hash_empty()
    {
        var handler = CreateHandler(new StubQueryProcessor(null), new CapturingCommandBus());

        var request = new RequestResendCode
        {
            PhoneNumber = PhoneNumber,
            PhoneCodeHash = string.Empty
        };

        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PHONE_CODE_HASH_EMPTY");
    }

    // Requirement 1.5: an expired App_Code is rejected with 400 PHONE_CODE_EXPIRED and no command
    // is published.
    [Fact]
    public void Expired_app_code_raises_phone_code_expired()
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var expiredAppCode = new FakeAppCodeReadModel
        {
            Id = PhoneCodeHash,
            AppCodeId = PhoneCodeHash,
            Code = "11111",
            CreationTime = now - 600,
            Expire = now - 60, // already expired
            PhoneCodeHash = PhoneCodeHash,
            PhoneNumber = PhoneNumber,
            Email = null
        };

        var commandBus = new CapturingCommandBus();
        var handler = CreateHandler(new StubQueryProcessor(expiredAppCode), commandBus);

        var request = new RequestResendCode
        {
            PhoneNumber = PhoneNumber,
            PhoneCodeHash = PhoneCodeHash
        };

        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");
        commandBus.Published.Count.ShouldBe(0);
    }

    // Requirement 1.5: a missing App_Code (query returns null) is also rejected with
    // 400 PHONE_CODE_EXPIRED.
    [Fact]
    public void Missing_app_code_raises_phone_code_expired()
    {
        var commandBus = new CapturingCommandBus();
        var handler = CreateHandler(new StubQueryProcessor(null), commandBus);

        var request = new RequestResendCode
        {
            PhoneNumber = PhoneNumber,
            PhoneCodeHash = PhoneCodeHash
        };

        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");
        commandBus.Published.Count.ShouldBe(0);
    }

    private static object CreateHandler(IQueryProcessor queryProcessor, ICommandBus commandBus)
    {
        var codeGenerator = new StubVerificationCodeGenerator("22222");
        var options = new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(
            new MyTelegramMessengerServerOptions
            {
                CheckPhoneNumberFormat = false,
                VerificationCodeExpirationSeconds = 300
            });
        var countryHelper = new NoopCountryHelper();

        // ResendCodeHandler is internal sealed; construct it through the Messenger assembly.
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.ResendCodeHandler",
            throwOnError: true)!;
        return Activator.CreateInstance(
            type,
            queryProcessor,
            commandBus,
            codeGenerator,
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
