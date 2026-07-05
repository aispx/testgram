// Feature: auth-methods-completion, Property 8: Drop retains exactly the exception set and drops the rest
//
// For any set of account sessions with temp auth keys and for any exception set of key ids,
// after dropTempAuthKeys the retained temp keys are exactly those whose ids appear in the
// exception set, and every other temp key of the account is dropped (an empty exception set
// drops all temp keys).
//
// Validates: Requirements 4.1, 4.2, 4.3
//
// Approach: this single parametric property drives the production (internal)
// DropTempAuthKeysHandler via reflection (mirroring Property 1/2/3/4/6/7) with hand-rolled fakes:
//   * StubQueryProcessor returns a generated IReadOnlyCollection<IDeviceReadModel> for the
//     GetDeviceByUserIdQuery the handler issues (each device carries a distinct temp auth key id;
//     some are 0, meaning "no temp key").
//   * CapturingCommandBus records every published UnRegisterDeviceForAuthKeyCommand.
//   * CapturingEventBus records every published AuthKeyUnRegisteredIntegrationEvent (which carries
//     the perm/temp key ids of each dropped session).
//
// The generator produces a set of account devices (distinct non-zero temp key ids, plus some
// zero-temp devices) and an exception set that is a subset of the present temp keys (sometimes
// empty). The handler runs authenticated (input.UserId != 0). The property asserts that the set of
// DROPPED temp keys (from the captured integration events) equals exactly
// {device temp keys that are non-zero and NOT in except}, that no excepted (retained) key was
// dropped, that no zero temp key was ever dropped, and that the number of published commands equals
// the number of dropped keys (one command per dropped key, no duplicates).

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
using MyTelegram.Core;
using MyTelegram.EventBus;
using MyTelegram.Messenger;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property08_DropExceptPartitionTests
{
    // Property 8: Drop retains exactly the exception set and drops the rest
    // Validates: Requirements 4.1, 4.2, 4.3
    [Property(Arbitrary = new[] { typeof(DropCaseArbitraries) }, MaxTest = 100)]
    public void Drop_retains_exactly_the_exception_set_and_drops_the_rest(DropCase testCase)
    {
        // Arrange: the account's sessions and a capturing command/event bus.
        var devices = testCase.Devices
            .Select(d => (IDeviceReadModel)new FakeDeviceReadModel
            {
                Id = d.PermAuthKeyId.ToString(),
                PermAuthKeyId = d.PermAuthKeyId,
                TempAuthKeyId = d.TempAuthKeyId,
                UserId = 1L
            })
            .ToList();

        var queryProcessor = new StubQueryProcessor(devices);
        var commandBus = new CapturingCommandBus();
        var eventBus = new CapturingEventBus();

        // Constructor arg order: (IQueryProcessor queryProcessor, ICommandBus commandBus, IEventBus eventBus)
        var handler = CreateMessengerHandler(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.DropTempAuthKeysHandler",
            queryProcessor,
            commandBus,
            eventBus);

        var request = new RequestDropTempAuthKeys
        {
            ExceptAuthKeys = new TVector<long>(testCase.ExceptKeys)
        };

        // Authenticated caller (input.UserId != 0) so the drop actually runs (Requirement 4 happy path).
        var input = CreateRequestInput(userId: 1L);

        // Act: invoke the handler and confirm it returns TBoolTrue (wrapped in the RPC result envelope).
        var result = InvokeAsync(handler, input, request);
        var rpcResult = result.ShouldBeOfType<TRpcResult>();
        rpcResult.Result.ShouldBeOfType<TBoolTrue>();

        // Expected: every non-zero temp key NOT in the exception set is dropped; excepted (and zero)
        // temp keys are retained.
        var exceptSet = new HashSet<long>(testCase.ExceptKeys);
        var expectedDropped = testCase.Devices
            .Where(d => d.TempAuthKeyId != 0 && !exceptSet.Contains(d.TempAuthKeyId))
            .Select(d => d.TempAuthKeyId)
            .ToHashSet();

        // The set of dropped temp keys (from the captured integration events) equals exactly the
        // expected set (Requirements 4.1, 4.2, 4.3).
        var droppedTempKeys = eventBus.Events.Select(e => e.TempAuthKeyId).ToHashSet();
        droppedTempKeys.ShouldBe(expectedDropped, ignoreOrder: true);

        // No excepted key was dropped (Requirement 4.3: every excepted key is retained).
        foreach (var exceptedKey in exceptSet)
        {
            droppedTempKeys.ShouldNotContain(exceptedKey);
        }

        // A zero temp key means "no temp key" and must never be dropped.
        droppedTempKeys.ShouldNotContain(0L);

        // Exactly one UnRegisterDeviceForAuthKeyCommand is published per dropped key (no duplicates,
        // and one integration event per command).
        commandBus.Published.Count.ShouldBe(expectedDropped.Count);
        eventBus.Events.Count.ShouldBe(expectedDropped.Count);

        // Cross-check: the perm keys targeted by the integration events are exactly the perm keys of
        // the dropped devices.
        var expectedDroppedPermKeys = testCase.Devices
            .Where(d => d.TempAuthKeyId != 0 && !exceptSet.Contains(d.TempAuthKeyId))
            .Select(d => d.PermAuthKeyId)
            .ToHashSet();
        var droppedPermKeys = eventBus.Events.Select(e => e.PermAuthKeyId).ToHashSet();
        droppedPermKeys.ShouldBe(expectedDroppedPermKeys, ignoreOrder: true);
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

    private static RequestInput CreateRequestInput(long userId)
    {
        return new RequestInput(
            ConnectionId: "test-connection",
            ConnectionType: default,
            RequestId: Guid.NewGuid(),
            ObjectId: 0u,
            ReqMsgId: 1L,
            SeqNumber: 0,
            UserId: userId,
            AuthKeyId: 1L,
            PermAuthKeyId: 1L,
            Layer: 0,
            Date: 0L,
            DeviceType: default,
            ClientIp: "127.0.0.1",
            SessionId: 1L,
            AccessHashKeyId: 0L);
    }

    /// <summary>Reflectively constructs an internal sealed handler from the Messenger assembly.</summary>
    private static object CreateMessengerHandler(string typeName, params object[] args)
    {
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(type, args)!;
    }

    /// <summary>Returns the configured device collection for the GetDeviceByUserIdQuery the handler
    /// issues.</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Captures published commands so the test can assert one per dropped key.</summary>
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

    /// <summary>Captures published AuthKeyUnRegisteredIntegrationEvents (the dropped keys).</summary>
    private sealed class CapturingEventBus : IEventBus
    {
        public List<AuthKeyUnRegisteredIntegrationEvent> Events { get; } = new();

        public Task PublishAsync<TEventData>(TEventData eventData, string? eventType = null)
            where TEventData : class
        {
            if (eventData is AuthKeyUnRegisteredIntegrationEvent e)
            {
                Events.Add(e);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeviceReadModel : IDeviceReadModel
    {
        public int ApiId { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public int DateActive { get; set; }
        public int DateCreated { get; set; }
        public string DeviceModel { get; set; } = string.Empty;
        public long Hash { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string LangCode { get; set; } = string.Empty;
        public string LangPack { get; set; } = string.Empty;
        public int Layer { get; set; }
        public bool OfficialApp { get; set; }
        public bool PasswordPending { get; set; }
        public long PermAuthKeyId { get; set; }
        public string Platform { get; set; } = string.Empty;
        public string SystemLangCode { get; set; } = string.Empty;
        public string SystemVersion { get; set; } = string.Empty;
        public long TempAuthKeyId { get; set; }
        public long UserId { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }
}

/// <summary>A single account session: a distinct perm auth key id and its temp auth key id (0 means
/// no temp key).</summary>
public sealed record DropDevice(long PermAuthKeyId, long TempAuthKeyId);

/// <summary>Input case for Property 8: the account's sessions and the exception set of temp key ids
/// (a subset of the present temp keys, sometimes empty).</summary>
public sealed record DropCase(
    IReadOnlyList<DropDevice> Devices,
    IReadOnlyList<long> ExceptKeys);

/// <summary>FsCheck arbitrary surface for Property 8. Generates 0-8 sessions with DISTINCT non-zero
/// temp key ids (with a fraction randomly zeroed to model sessions without a temp key) and DISTINCT
/// perm key ids, plus an exception set that is a subset of the present non-zero temp keys (empty
/// exception sets --- which drop everything --- arise naturally).</summary>
public static class DropCaseArbitraries
{
    public static Arbitrary<DropCase> DropCase()
    {
        var gen =
            from count in Gen.Choose(0, 8)
            from tempCandidates in Gen.ArrayOf(count, Gen.Choose(1, 100_000).Select(i => (long)i))
            // ~1 in 5 sessions has no temp key (temp id 0).
            from zeroFlags in Gen.ArrayOf(count, Gen.Frequency(
                Tuple.Create(1, Gen.Constant(true)),
                Tuple.Create(4, Gen.Constant(false))))
            // Per-session flag for whether its temp key is added to the exception set.
            from exceptFlags in Gen.ArrayOf(count, Gen.Elements(true, false))
            select BuildCase(tempCandidates, zeroFlags, exceptFlags);

        return Arb.From(gen);
    }

    private static DropCase BuildCase(long[] tempCandidates, bool[] zeroFlags, bool[] exceptFlags)
    {
        var devices = new List<DropDevice>();
        var exceptKeys = new List<long>();
        var usedTempKeys = new HashSet<long>();

        for (var i = 0; i < tempCandidates.Length; i++)
        {
            // Distinct perm key id per session.
            var permKeyId = i + 1L;

            long tempKeyId;
            if (zeroFlags[i])
            {
                tempKeyId = 0L; // session with no temp key
            }
            else
            {
                // Ensure distinct non-zero temp key ids; skip candidates already used.
                if (!usedTempKeys.Add(tempCandidates[i]))
                {
                    continue;
                }

                tempKeyId = tempCandidates[i];
            }

            devices.Add(new DropDevice(permKeyId, tempKeyId));

            // Only non-zero temp keys can be excepted (retained).
            if (tempKeyId != 0 && exceptFlags[i])
            {
                exceptKeys.Add(tempKeyId);
            }
        }

        return new DropCase(devices, exceptKeys);
    }
}
