using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using StatsSchema = MyTelegram.Schema.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 18: Handlers delegate and never throw <c>NotImplementedException</c>.
///
/// For any of the seven stats requests that passes access control, the handler returns a non-null schema
/// result object produced by the Stats_Service rather than throwing <c>NotImplementedException</c>.
///
/// Validates: Requirements 12.1.
///
/// <para>Each of the seven production handlers in
/// <c>MyTelegram.Messenger/Handlers/LatestLayer/Stats/</c> is <c>internal sealed</c> with a
/// primary-constructor injecting <see cref="IStatsAccessController"/> and/or <see cref="IStatsService"/>,
/// and a <c>protected override Task&lt;TResult&gt; HandleCoreAsync(IRequestInput, TRequest)</c>. Rather
/// than adding <c>InternalsVisibleTo</c> to the production assembly, this test constructs each handler
/// reflectively from the Messenger assembly and drives it through the public
/// <see cref="IObjectHandler.HandleAsync"/> entry point (which every handler implements via its public
/// base class), then unwraps the <see cref="TRpcResult"/> the base wraps the result in.</para>
///
/// <para>The collaborators are stubs modelling the "passes access control" precondition: the
/// <see cref="IStatsAccessController"/> resolves an arbitrary channel/peer without throwing, and the
/// <see cref="IStatsService"/> returns a distinct, non-null sentinel schema object per method. The
/// property then asserts, for every handler, that (a) no <see cref="NotImplementedException"/> is thrown
/// and (b) the object the handler returns is <em>reference-identical</em> to the sentinel the service
/// produced — i.e. the handler genuinely delegates and returns the service's object unchanged. Request
/// field values (dark flag, ids, offsets, limit, token, zoom x) are varied by the generator so delegation
/// is exercised across the input space. Each run executes a minimum of 100 generated cases.</para>
/// </summary>
public class HandlerDelegationPropertyTests
{
    private const string HandlerNamespace = "MyTelegram.Messenger.Handlers.LatestLayer.Stats";

    [Property(MaxTest = 100)]
    public void Handlers_delegate_and_never_throw_not_implemented(
        bool dark,
        long channelId,
        int msgId,
        int storyId,
        NonNull<string> offset,
        int limit,
        NonNull<string> token,
        bool hasX,
        long x)
    {
        // A per-run stub service handing back a distinct, non-null sentinel object for each of the seven
        // service methods; the handler must return exactly the object its delegated method produced.
        var service = new StubStatsService();

        // The Access_Controller stub represents a request that PASSES access control: it resolves the
        // target without raising, so the handler proceeds to delegate to the Stats_Service.
        var accessController = new Mock<IStatsAccessController>(MockBehavior.Strict);
        accessController
            .Setup(x => x.ResolveChannelForStatsAsync(
                It.IsAny<IRequestInput>(), It.IsAny<IInputChannel>(), It.IsAny<StatsChannelKind>(), It.IsAny<bool>()))
            .ReturnsAsync(Mock.Of<IChannelReadModel>(m => m.ChannelId == channelId));
        accessController
            .Setup(x => x.ResolvePeerForStoryStatsAsync(It.IsAny<IRequestInput>(), It.IsAny<IInputPeer>()))
            .ReturnsAsync(new Peer(PeerType.Channel, channelId));

        var input = new TestRequestInput(userId: 42, layer: 195);
        var offsetValue = offset.Get;
        var tokenValue = token.Get;
        long? xValue = hasX ? x : null;

        var inputChannel = new TInputChannel { ChannelId = channelId, AccessHash = 0 };
        var inputPeer = new TInputPeerChannel { ChannelId = channelId, AccessHash = 0 };

        // (handler type name, constructor args, request object, expected sentinel object)
        var cases = new (string HandlerName, object[] CtorArgs, IObject Request, IObject Expected)[]
        {
            (
                "GetBroadcastStatsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetBroadcastStats { Dark = dark, Channel = inputChannel },
                service.BroadcastStats),
            (
                "GetMegagroupStatsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetMegagroupStats { Dark = dark, Channel = inputChannel },
                service.MegagroupStats),
            (
                "GetMessageStatsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetMessageStats { Dark = dark, Channel = inputChannel, MsgId = msgId },
                service.MessageStats),
            (
                "GetStoryStatsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetStoryStats { Dark = dark, Peer = inputPeer, Id = storyId },
                service.StoryStats),
            (
                "GetMessagePublicForwardsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetMessagePublicForwards
                {
                    Channel = inputChannel, MsgId = msgId, Offset = offsetValue, Limit = limit
                },
                service.MessagePublicForwards),
            (
                "GetStoryPublicForwardsHandler",
                new object[] { accessController.Object, service },
                new StatsSchema.RequestGetStoryPublicForwards
                {
                    Peer = inputPeer, Id = storyId, Offset = offsetValue, Limit = limit
                },
                service.StoryPublicForwards),
            (
                // LoadAsyncGraphHandler skips access control and injects only the Stats_Service.
                "LoadAsyncGraphHandler",
                new object[] { service },
                new StatsSchema.RequestLoadAsyncGraph { Token = tokenValue, X = xValue },
                service.AsyncGraph),
        };

        foreach (var (handlerName, ctorArgs, request, expected) in cases)
        {
            var handler = CreateHandler(handlerName, ctorArgs);

            IObject? wrapped;
            try
            {
                wrapped = handler.HandleAsync(input, request).GetAwaiter().GetResult();
            }
            catch (NotImplementedException ex)
            {
                // A NotImplementedException means the handler is still a stub and does not delegate
                // (Requirement 12.1). Surface it as a property failure with the offending handler name.
                throw new Xunit.Sdk.XunitException(
                    $"{handlerName} threw NotImplementedException instead of delegating to the Stats_Service: {ex}");
            }

            wrapped.ShouldNotBeNull($"{handlerName} returned a null result");

            // The base RpcResultObjectHandler wraps the schema result in a TRpcResult.
            var rpcResult = wrapped.ShouldBeOfType<TRpcResult>();

            // The handler must return the exact object the delegated Stats_Service method produced.
            rpcResult.Result.ShouldBeSameAs(expected,
                $"{handlerName} did not return the object produced by the Stats_Service");
        }
    }

    /// <summary>
    /// Reflectively constructs an <c>internal sealed</c> stats handler from the Messenger assembly and
    /// returns it as the public <see cref="IObjectHandler"/> it implements. The Messenger assembly is
    /// located via the public <see cref="StatsService"/> type; the handler's primary constructor is invoked
    /// with the supplied dependency stubs (its accessibility matches the internal type, so non-public
    /// binding is required).
    /// </summary>
    private static IObjectHandler CreateHandler(string handlerName, object[] ctorArgs)
    {
        var assembly = typeof(StatsService).Assembly;
        var type = assembly.GetType($"{HandlerNamespace}.{handlerName}", throwOnError: true)!;

        var instance = Activator.CreateInstance(
            type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ctorArgs,
            culture: null)!;

        return (IObjectHandler)instance;
    }

    /// <summary>
    /// A stub <see cref="IStatsService"/> returning a distinct, non-null sentinel schema object per method
    /// so each handler's delegation target can be checked by reference identity. No method throws, modelling
    /// a successful assembly.
    /// </summary>
    private sealed class StubStatsService : IStatsService
    {
        public StatsSchema.IBroadcastStats BroadcastStats { get; } = new StatsSchema.TBroadcastStats();
        public StatsSchema.IMegagroupStats MegagroupStats { get; } = new StatsSchema.TMegagroupStats();
        public StatsSchema.IMessageStats MessageStats { get; } = new StatsSchema.TMessageStats();
        public StatsSchema.IStoryStats StoryStats { get; } = new StatsSchema.TStoryStats();
        public StatsSchema.IPublicForwards MessagePublicForwards { get; } = new StatsSchema.TPublicForwards();
        public StatsSchema.IPublicForwards StoryPublicForwards { get; } = new StatsSchema.TPublicForwards();
        public IStatsGraph AsyncGraph { get; } = new TStatsGraph();

        public Task<StatsSchema.IBroadcastStats> GetBroadcastStatsAsync(IRequestInput input, long channelId, bool dark) =>
            Task.FromResult(BroadcastStats);

        public Task<StatsSchema.IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark) =>
            Task.FromResult(MegagroupStats);

        public Task<StatsSchema.IMessageStats> GetMessageStatsAsync(IRequestInput input, long channelId, int msgId, bool dark) =>
            Task.FromResult(MessageStats);

        public Task<StatsSchema.IStoryStats> GetStoryStatsAsync(IRequestInput input, Peer peer, int storyId, bool dark) =>
            Task.FromResult(StoryStats);

        public Task<StatsSchema.IPublicForwards> GetMessagePublicForwardsAsync(IRequestInput input, long channelId, int msgId, string offset, int limit) =>
            Task.FromResult(MessagePublicForwards);

        public Task<StatsSchema.IPublicForwards> GetStoryPublicForwardsAsync(IRequestInput input, Peer peer, int storyId, string offset, int limit) =>
            Task.FromResult(StoryPublicForwards);

        public Task<IStatsGraph> LoadAsyncGraphAsync(IRequestInput input, string token, long? x) =>
            Task.FromResult(AsyncGraph);
    }

    /// <summary>
    /// Minimal <see cref="IRequestInput"/> carrying only the user id and layer a handler forwards to the
    /// service; all other members are inert defaults.
    /// </summary>
    private sealed class TestRequestInput(long userId, int layer) : IRequestInput
    {
        public long UserId { get; } = userId;
        public int Layer { get; set; } = layer;
        public string ConnectionId => string.Empty;
        public ConnectionType ConnectionType => default;
        public long AuthKeyId => 0;
        public uint ObjectId { get; set; }
        public long PermAuthKeyId => 0;
        public long ReqMsgId => 0;
        public int SeqNumber => 0;
        public Guid RequestId => Guid.Empty;
        public long Date => 0;
        public DeviceType DeviceType { get; set; }
        public string ClientIp => string.Empty;
        public long SessionId => 0;
        public long AccessHashKeyId { get; set; }
    }
}
