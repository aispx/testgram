// Feature: auth-methods-completion, Property 9: Drop requires authentication
//
// For any dropTempAuthKeys request from an unauthenticated caller (no bound user, i.e.
// input.UserId == 0), the server raises AUTH_KEY_UNREGISTERED and drops no keys.
//
// Validates: Requirements 4.4
//
// Approach: this single parametric property drives the production (internal)
// DropTempAuthKeysHandler via reflection (mirroring Property 8) with hand-rolled fakes:
//   * StubQueryProcessor would return a generated IReadOnlyCollection<IDeviceReadModel> for the
//     GetDeviceByUserIdQuery -- but on the unauthenticated path the handler must short-circuit
//     BEFORE issuing any query, so these devices must never be dropped.
//   * CapturingCommandBus records every published UnRegisterDeviceForAuthKeyCommand (must stay empty).
//   * CapturingEventBus records every published AuthKeyUnRegisteredIntegrationEvent (must stay empty).
//
// The generator produces an arbitrary exception set and an arbitrary list of account sessions
// (each with a temp auth key id, some 0), but the handler ALWAYS runs with input.UserId == 0. The
// property asserts that 401 AUTH_KEY_UNREGISTERED is raised and that NO command and NO event were
// published (no keys dropped).

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

public class Property09_DropRequiresAuthTests
{
    // Property 9: Drop requires authentication
    // Validates: Requirements 4.4
    [Property(Arbitrary = new[] { typeof(DropUnauthenticatedArbitraries) }, MaxTest = 100)]
    public void Drop_requires_authentication(DropUnauthenticatedCase testCase)
    {
        // Arrange: an arbitrary set of account sessions and an arbitrary exception set. On the
        // unauthenticated path the handler must never touch any of these.
        var devices = testCase.Devices
            .Select(d => (IDeviceReadModel)new FakeDeviceReadModel
            {
                Id = d.PermAuthKeyId.ToString(),
                PermAuthKeyId = d.PermAuthKeyId,
                TempAuthKeyId = d.TempAuthKeyId,
                UserId = 0L
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

        // Unauthenticated caller (input.UserId == 0): no bound user (Requirement 4.4).
        var input = CreateRequestInput(userId: 0L);

        // Act + Assert: the handler raises 401 AUTH_KEY_UNREGISTERED.
        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, input, request));
        ex.RpcError.ErrorCode.ShouldBe(401);
        ex.RpcError.Message.ShouldBe("AUTH_KEY_UNREGISTERED");

        // No keys dropped: neither a command nor an integration event is published.
        commandBus.Published.Count.ShouldBe(0);
        eventBus.Events.Count.ShouldBe(0);
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
    /// would issue (it must not be queried on the unauthenticated path).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
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

    /// <summary>Captures published AuthKeyUnRegisteredIntegrationEvents so the test can assert none.</summary>
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

/// <summary>Input case for Property 9: an arbitrary set of account sessions and an arbitrary
/// exception set of temp key ids. The handler always runs unauthenticated (input.UserId == 0), so
/// neither collection should ever be acted upon.</summary>
public sealed record DropUnauthenticatedCase(
    IReadOnlyList<DropDevice> Devices,
    IReadOnlyList<long> ExceptKeys);

/// <summary>FsCheck arbitrary surface for Property 9. Generates 0-8 sessions with DISTINCT non-zero
/// temp key ids (a fraction randomly zeroed to model sessions without a temp key) and DISTINCT perm
/// key ids, plus an arbitrary exception set of longs. Because the caller is unauthenticated, the
/// exact contents are irrelevant to the outcome (no keys are ever dropped) -- they exist only to
/// prove the auth check fires regardless of the request payload.</summary>
public static class DropUnauthenticatedArbitraries
{
    public static Arbitrary<DropUnauthenticatedCase> DropUnauthenticatedCase()
    {
        var gen =
            from count in Gen.Choose(0, 8)
            from tempCandidates in Gen.ArrayOf(count, Gen.Choose(1, 100_000).Select(i => (long)i))
            // ~1 in 5 sessions has no temp key (temp id 0).
            from zeroFlags in Gen.ArrayOf(count, Gen.Frequency(
                Tuple.Create(1, Gen.Constant(true)),
                Tuple.Create(4, Gen.Constant(false))))
            // An arbitrary exception set (0-8 arbitrary key ids).
            from exceptCount in Gen.Choose(0, 8)
            from exceptKeys in Gen.ArrayOf(exceptCount, Gen.Choose(1, 100_000).Select(i => (long)i))
            select BuildCase(tempCandidates, zeroFlags, exceptKeys);

        return Arb.From(gen);
    }

    private static DropUnauthenticatedCase BuildCase(long[] tempCandidates, bool[] zeroFlags, long[] exceptKeys)
    {
        var devices = new List<DropDevice>();
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
        }

        return new DropUnauthenticatedCase(devices, exceptKeys.Distinct().ToList());
    }
}
