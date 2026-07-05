// Feature: auth-methods-completion, Property 4: Cancel invalidates a valid code and is a no-op on expired/missing codes
//
// For any valid, non-expired App_Code, cancelCode returns TBoolTrue and leaves the App_Code in the
// cancelled state (realised here by publishing a CancelCodeCommand, which drives
// AppCodeAggregate.CancelCode -> AppCodeCanceledEvent -> Canceled = true); for any phone number and
// Phone_Code_Hash whose App_Code is missing or already expired, cancelCode raises
// 400 PHONE_CODE_EXPIRED and emits no events (publishes no command).
//
// Validates: Requirements 2.1, 2.4, 2.5
//
// Approach: the property drives the production (internal) CancelCodeHandler via reflection
// (mirroring Property 1/2) with hand-rolled fakes: a query processor returning the generated
// App_Code (or null), and a capturing command bus. A single generator produces all three reachable
// states — a valid non-expired App_Code, a missing (null) App_Code, and an already-expired
// App_Code — using numeric phone numbers (the handler validates the phone number with long.TryParse)
// and non-empty phone code hashes. For the valid case it asserts TBoolTrue is returned AND exactly
// one CancelCodeCommand was published; for the missing/expired cases it asserts 400
// PHONE_CODE_EXPIRED and that NO command was published.

using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property04_CancelBehaviorTests
{
    // Property 4: Cancel invalidates a valid code and is a no-op on expired/missing codes
    // Validates: Requirements 2.1, 2.4, 2.5
    [Property(Arbitrary = new[] { typeof(CancelArbitraries) }, MaxTest = 100)]
    public void Cancel_invalidates_valid_code_and_is_noop_on_expired_or_missing(CancelCase testCase)
    {
        // Arrange: build the App_Code state the query processor will return for this case.
        var now = DateTime.UtcNow.ToTimestamp();
        IAppCodeReadModel? appCode = testCase.Kind switch
        {
            // (2.4/2.5) missing App_Code -> query returns null.
            CancelCaseKind.Missing => null,
            // (2.4) already-expired App_Code -> Expire is in the past.
            CancelCaseKind.Expired => new FakeAppCodeReadModel
            {
                Id = testCase.PhoneCodeHash,
                AppCodeId = testCase.PhoneCodeHash,
                Code = "11111",
                CreationTime = now,
                Expire = now - testCase.Offset, // already expired
                PhoneCodeHash = testCase.PhoneCodeHash,
                PhoneNumber = testCase.PhoneNumber
            },
            // (2.1) valid, non-expired App_Code -> Expire well ahead of now.
            _ => new FakeAppCodeReadModel
            {
                Id = testCase.PhoneCodeHash,
                AppCodeId = testCase.PhoneCodeHash,
                Code = "11111",
                CreationTime = now,
                Expire = now + testCase.Offset,
                PhoneCodeHash = testCase.PhoneCodeHash,
                PhoneNumber = testCase.PhoneNumber
            }
        };

        var queryProcessor = new StubQueryProcessor(appCode);
        var commandBus = new CapturingCommandBus();

        var handler = CreateCancelCodeHandler(queryProcessor, commandBus);
        var request = new RequestCancelCode
        {
            PhoneNumber = testCase.PhoneNumber,
            PhoneCodeHash = testCase.PhoneCodeHash
        };

        if (testCase.Kind == CancelCaseKind.Valid)
        {
            // Requirement 2.1: TBoolTrue is returned AND the App_Code is driven to the cancelled
            // state via a published CancelCodeCommand.
            var result = InvokeAsync(handler, CreateRequestInput(), request);
            result.ShouldBeOfType<TBoolTrue>();

            commandBus.Published.Count.ShouldBe(1);
            commandBus.Published[0].GetType().Name.ShouldBe("CancelCodeCommand");
        }
        else
        {
            // Requirements 2.4 / 2.5: a missing or expired App_Code raises 400 PHONE_CODE_EXPIRED
            // and emits no events (no command is published -> no state change).
            var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, CreateRequestInput(), request));
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");

            commandBus.Published.Count.ShouldBe(0);
        }
    }

    private static object CreateCancelCodeHandler(IQueryProcessor queryProcessor, ICommandBus commandBus)
    {
        // CancelCodeHandler is internal sealed; construct it through the Messenger assembly.
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.CancelCodeHandler",
            throwOnError: true)!;
        return Activator.CreateInstance(type, queryProcessor, commandBus)!;
    }

    private static IBool InvokeAsync(object handler, IRequestInput input, IObject request)
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
        return (IBool)rpcResult.Result;
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

/// <summary>The three reachable states exercised by Property 4: a valid non-expired App_Code, a
/// missing (null) App_Code, and an already-expired App_Code.</summary>
public enum CancelCaseKind
{
    Valid,
    Missing,
    Expired
}

/// <summary>Input case for Property 4: the state kind plus a numeric phone number (accepted by the
/// handler's long.TryParse validation), a non-empty phone code hash, and a positive offset used to
/// place Expire in the future (valid) or past (expired).</summary>
public sealed record CancelCase(CancelCaseKind Kind, string PhoneNumber, string PhoneCodeHash, int Offset);

/// <summary>FsCheck arbitrary surface for Property 4. Generates each of the three reachable states
/// together with a numeric phone number, a non-empty phone code hash, and a positive expiry offset.</summary>
public static class CancelArbitraries
{
    public static Arbitrary<CancelCase> CancelCase()
    {
        var kindGen = Gen.Elements(CancelCaseKind.Valid, CancelCaseKind.Missing, CancelCaseKind.Expired);
        var phoneGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var offsetGen = Gen.Choose(60, 1_000_000);

        var gen =
            from kind in kindGen
            from phone in phoneGen
            from hash in hashGen
            from offset in offsetGen
            select new CancelCase(kind, phone, hash, offset);

        return Arb.From(gen);
    }
}
