using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using StatsSchema = MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 20: Service errors surface as RPC errors, never partial results.
///
/// If the Access_Controller or the Stats_Service reports an error condition while processing a request,
/// the handler returns the corresponding RPC error to the caller rather than propagating an unhandled
/// exception, and does not return a partially populated result object. The two public-forwards handlers
/// additionally map an <see cref="InvalidStatsOffsetException"/> raised by the service to
/// <c>OFFSET_INVALID</c> (Requirement 6.8).
///
/// Validates: Requirements 12.4.
///
/// Each of the seven handlers in <c>MyTelegram.Messenger/Handlers/LatestLayer/Stats</c> is constructed with
/// hand-written stubs for its <see cref="IStatsAccessController"/> and <see cref="IStatsService"/>
/// collaborators. The <see cref="ErrorSurfacingArbitraries"/> generator independently selects which of the
/// seven handlers is exercised, which collaborator raises the error (the access controller resolves first,
/// then the service — <c>LoadAsyncGraph</c> has no access-control step so its error always originates in the
/// service), and which error condition that collaborator raises (drawn from the full pool of stats RPC
/// errors, plus the invalid-offset exception for the public-forwards handlers). The handler's protected
/// <c>HandleCoreAsync</c> is invoked via reflection so the test is self-contained and does not depend on
/// any <c>InternalsVisibleTo</c> wiring. The property asserts that:
/// <list type="bullet">
///   <item>the awaited handler throws an <see cref="RpcException"/> whose code and message equal the
///   expected error (the raised error for a direct throw; <c>OFFSET_INVALID</c> for the mapped
///   invalid-offset case) — i.e. the error surfaces as an RPC error, never as an unhandled exception nor a
///   (partial) result object;</item>
///   <item>when the access controller raises, the service is never consulted (no partial assembly begins);</item>
///   <item>when the service raises, the access controller had already resolved successfully and the service
///   was reached — yet still no result object is produced.</item>
/// </list>
/// Each run executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(ErrorSurfacingArbitraries) }, MaxTest = 100)]
public class ErrorSurfacingPropertyTests
{
    private static readonly Assembly MessengerAssembly = typeof(IStatsService).Assembly;

    [Property]
    public void Service_and_access_errors_surface_as_rpc_errors_without_partial_results(ErrorSurfacingCase @case)
    {
        // The error the selected collaborator raises: either an RPC error thrown directly, or (for the
        // public-forwards handlers) the invalid-offset exception the handler maps to OFFSET_INVALID.
        Exception thrown = @case.UseInvalidOffsetException
            ? new InvalidStatsOffsetException("unrecognized_cursor")
            : new RpcException(@case.Error);

        // The RPC error the caller must observe. A direct RPC error surfaces unchanged; the invalid-offset
        // exception is mapped to OFFSET_INVALID (Requirement 6.8).
        RpcError expected = @case.UseInvalidOffsetException
            ? RpcErrors.RpcErrors400.OffsetInvalid
            : @case.Error;

        ConfigurableAccessController accessController;
        ThrowingStatsService service;
        if (@case.Origin == ErrorOrigin.AccessController)
        {
            // The access controller raises; the service must never be reached, so give it a sentinel
            // exception that would fail the test loudly if it were ever invoked.
            accessController = new ConfigurableAccessController(thrown);
            service = new ThrowingStatsService(
                new InvalidOperationException("Stats_Service must not be called after an access-control failure."));
        }
        else
        {
            // The access controller resolves successfully; the service raises.
            accessController = new ConfigurableAccessController(null);
            service = new ThrowingStatsService(thrown);
        }

        var handlerType = HandlerType(@case.HandlerName);
        var handler = CreateHandler(handlerType, accessController, service);
        var request = CreateRequest(@case.HandlerName);

        var method = handlerType.GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull($"{@case.HandlerName} must expose HandleCoreAsync");

        var input = Mock.Of<IRequestInput>(x => x.UserId == 42L);

        // Invoking an async method via reflection returns the (possibly already-faulted) Task; awaiting it
        // rethrows the captured exception. A thrown exception means no result object was returned.
        var task = (Task)method!.Invoke(handler, new object?[] { input, request })!;

        var ex = Should.Throw<RpcException>(() => task.GetAwaiter().GetResult());

        // The error surfaced as an RPC error with the expected code/message (never an unhandled exception).
        ex.RpcError.ErrorCode.ShouldBe(expected.ErrorCode);
        ex.RpcError.Message.ShouldBe(expected.Message);

        if (@case.Origin == ErrorOrigin.AccessController)
        {
            // An access-control failure short-circuits before any result assembly begins.
            service.WasCalled.ShouldBeFalse();
        }
        else
        {
            // The service was reached (after a successful resolve for access-controlled handlers), yet the
            // handler still returned no result object — the error superseded any partial result.
            if (@case.HandlerHasAccessControl)
            {
                accessController.Resolved.ShouldBeTrue();
            }

            service.WasCalled.ShouldBeTrue();
        }
    }

    private static Type HandlerType(string name)
    {
        var type = MessengerAssembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Stats.{name}");
        type.ShouldNotBeNull($"Handler type {name} must exist");
        return type!;
    }

    /// <summary>
    /// Constructs a handler instance, matching each constructor parameter by its type so both the
    /// (access controller + service) handlers and the service-only <c>LoadAsyncGraphHandler</c> are handled.
    /// </summary>
    private static object CreateHandler(Type handlerType, IStatsAccessController accessController, IStatsService service)
    {
        var ctor = handlerType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();

        var args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(IStatsAccessController)
                ? (object)accessController
                : service)
            .ToArray();

        return ctor.Invoke(args);
    }

    /// <summary>
    /// Builds a minimal request object for the handler. The collaborators are stubbed and ignore their
    /// inputs, so only enough of each request to reach the delegation call is populated.
    /// </summary>
    private static object CreateRequest(string handlerName) => handlerName switch
    {
        "GetBroadcastStatsHandler" => new StatsSchema.RequestGetBroadcastStats { Channel = InputChannel(), Dark = false },
        "GetMegagroupStatsHandler" => new StatsSchema.RequestGetMegagroupStats { Channel = InputChannel(), Dark = false },
        "GetMessageStatsHandler" => new StatsSchema.RequestGetMessageStats { Channel = InputChannel(), MsgId = 7, Dark = false },
        "GetStoryStatsHandler" => new StatsSchema.RequestGetStoryStats { Peer = null!, Id = 3, Dark = false },
        "GetMessagePublicForwardsHandler" => new StatsSchema.RequestGetMessagePublicForwards { Channel = InputChannel(), MsgId = 7, Offset = "", Limit = 20 },
        "GetStoryPublicForwardsHandler" => new StatsSchema.RequestGetStoryPublicForwards { Peer = null!, Id = 3, Offset = "", Limit = 20 },
        "LoadAsyncGraphHandler" => new StatsSchema.RequestLoadAsyncGraph { Token = "tok", X = null },
        _ => throw new ArgumentOutOfRangeException(nameof(handlerName), handlerName, null)
    };

    private static IInputChannel InputChannel() => new TInputChannel { ChannelId = 1, AccessHash = 0 };

    // ---- Stub collaborators ------------------------------------------------------------------

    /// <summary>
    /// An <see cref="IStatsAccessController"/> that either raises the configured exception from both resolve
    /// methods (simulating an access-control failure) or resolves successfully, recording that it did so.
    /// </summary>
    private sealed class ConfigurableAccessController(Exception? throwOnResolve) : IStatsAccessController
    {
        public bool Resolved { get; private set; }

        public Task<IChannelReadModel> ResolveChannelForStatsAsync(IRequestInput input, IInputChannel channel, StatsChannelKind requiredKind, bool checkJoinable)
        {
            if (throwOnResolve is not null)
            {
                throw throwOnResolve;
            }

            Resolved = true;
            return Task.FromResult(Mock.Of<IChannelReadModel>());
        }

        public Task<Peer> ResolvePeerForStoryStatsAsync(IRequestInput input, IInputPeer peer)
        {
            if (throwOnResolve is not null)
            {
                throw throwOnResolve;
            }

            Resolved = true;
            return Task.FromResult(new Peer(PeerType.Channel, 1234));
        }
    }

    /// <summary>
    /// An <see cref="IStatsService"/> whose every method records that it was reached and then raises the
    /// configured exception, modelling a service that reports an error condition mid-assembly.
    /// </summary>
    private sealed class ThrowingStatsService(Exception toThrow) : IStatsService
    {
        public bool WasCalled { get; private set; }

        public Task<StatsSchema.IBroadcastStats> GetBroadcastStatsAsync(IRequestInput input, long channelId, bool dark)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<StatsSchema.IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<StatsSchema.IMessageStats> GetMessageStatsAsync(IRequestInput input, long channelId, int msgId, bool dark)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<StatsSchema.IStoryStats> GetStoryStatsAsync(IRequestInput input, Peer peer, int storyId, bool dark)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<StatsSchema.IPublicForwards> GetMessagePublicForwardsAsync(IRequestInput input, long channelId, int msgId, string offset, int limit)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<StatsSchema.IPublicForwards> GetStoryPublicForwardsAsync(IRequestInput input, Peer peer, int storyId, string offset, int limit)
        {
            WasCalled = true;
            throw toThrow;
        }

        public Task<IStatsGraph> LoadAsyncGraphAsync(IRequestInput input, string token, long? x)
        {
            WasCalled = true;
            throw toThrow;
        }
    }
}

/// <summary>Which collaborator raises the error condition for a given case.</summary>
public enum ErrorOrigin
{
    AccessController,
    Service
}

/// <summary>
/// One generated error-surfacing case: which handler, which collaborator raises, and which error condition
/// is raised (either a concrete RPC error or the invalid-offset exception the public-forwards handlers map).
/// </summary>
public sealed record ErrorSurfacingCase(
    string HandlerName,
    bool HandlerHasAccessControl,
    ErrorOrigin Origin,
    RpcError Error,
    bool UseInvalidOffsetException)
{
    public override string ToString() =>
        $"ErrorSurfacing({HandlerName}, origin={Origin}, " +
        $"error={(UseInvalidOffsetException ? "InvalidStatsOffsetException->OFFSET_INVALID" : Error.Message)})";
}

/// <summary>
/// FsCheck arbitrary surface for <see cref="ErrorSurfacingCase"/>. Independently selects the handler, the
/// origin of the failure, and the raised error condition (Property 20).
/// </summary>
public static class ErrorSurfacingArbitraries
{
    private sealed record HandlerMeta(string Name, bool HasAccessControl, bool IsPublicForwards);

    private static readonly HandlerMeta[] Handlers =
    {
        new("GetBroadcastStatsHandler", true, false),
        new("GetMegagroupStatsHandler", true, false),
        new("GetMessageStatsHandler", true, false),
        new("GetStoryStatsHandler", true, false),
        new("GetMessagePublicForwardsHandler", true, true),
        new("GetStoryPublicForwardsHandler", true, true),
        new("LoadAsyncGraphHandler", false, false)
    };

    // The full pool of stats RPC errors the collaborators can raise. The property is agnostic to which
    // specific error a given handler would raise in production; it asserts only that whatever error the
    // collaborator reports surfaces unchanged.
    private static readonly RpcError[] ErrorPool =
    {
        RpcErrors.RpcErrors400.ChannelInvalid,
        RpcErrors.RpcErrors400.ChannelPrivate,
        RpcErrors.RpcErrors400.ChatAdminRequired,
        RpcErrors.RpcErrors400.BroadcastRequired,
        RpcErrors.RpcErrors400.MegagroupRequired,
        RpcErrors.RpcErrors400.MessageIdInvalid,
        RpcErrors.RpcErrors400.PeerIdInvalid,
        RpcErrors.RpcErrors400.StoriesNeverCreated,
        RpcErrors.RpcErrors400.GraphInvalidReload,
        RpcErrors.RpcErrors400.GraphExpiredReload,
        RpcErrors.RpcErrors400.GraphOutdatedReload
    };

    public static Arbitrary<ErrorSurfacingCase> Cases() => Arb.From(CaseGen);

    private static Gen<ErrorSurfacingCase> CaseGen =>
        from handler in FsCheck.Gen.Elements(Handlers)
        from originIsAccessControl in Arb.Generate<bool>()
        from errorIndex in FsCheck.Gen.Choose(0, ErrorPool.Length - 1)
        from useOffset in Arb.Generate<bool>()
        select Build(handler, originIsAccessControl, errorIndex, useOffset);

    private static ErrorSurfacingCase Build(HandlerMeta handler, bool originIsAccessControl, int errorIndex, bool useOffset)
    {
        // LoadAsyncGraph has no access-control step, so its error always originates in the service.
        var origin = handler.HasAccessControl && originIsAccessControl
            ? ErrorOrigin.AccessController
            : ErrorOrigin.Service;

        // The invalid-offset mapping only applies to the public-forwards handlers, and only when the
        // service (which reads the page) raises it.
        var useInvalidOffset = useOffset && handler.IsPublicForwards && origin == ErrorOrigin.Service;

        return new ErrorSurfacingCase(
            handler.Name,
            handler.HasAccessControl,
            origin,
            ErrorPool[errorIndex],
            useInvalidOffset);
    }
}
