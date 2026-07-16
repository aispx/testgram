using System.Reflection;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Stats;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 11.7 — end-to-end handler smoke tests.
///
/// <para>Each of the seven <c>stats.*</c> handlers
/// (<c>MyTelegram.Messenger/Handlers/LatestLayer/Stats/</c>) is constructed with representative populated
/// dependencies and invoked through its core RPC path (<c>HandleCoreAsync</c>). The test asserts the
/// returned schema result object is well-formed — the exact TL result type the handler's contract promises,
/// with every non-optional field populated — and that it serializes without error via the TL serialization
/// API (<c>IObject.ToBytes()</c>, which calls <c>Serialize</c> and throws on a missing non-optional field).
/// This complements the deep serialization round-trip property (Property 19) with a fast, hermetic,
/// example-based smoke check that the wired handlers (Task 11.1/11.2) return serializable responses
/// (Requirements 12.1, 12.2).</para>
///
/// <para><b>Dependencies.</b> A <see cref="StubAccessController"/> resolves the channel/peer successfully
/// (the access-control branches are covered by Task 7.2/7.3), and a <see cref="PopulatedStatsService"/>
/// returns fully-populated, well-formed schema objects for a representative fixture per handler — a channel
/// with metrics and every graph field set (real <see cref="GraphBuilder"/> output), a message, a story, a
/// public-forward page, and an async graph. The handlers are <c>internal sealed</c> types and
/// <c>HandleCoreAsync</c> is <c>protected</c>, so both are reached via reflection (no
/// <c>InternalsVisibleTo</c> is required and none is added, keeping this file independent of the
/// concurrent Task 11.4/11.5/11.6 property tests).</para>
/// </summary>
public class EndToEndHandlerSmokeTests
{
    private const string HandlerNamespace = "MyTelegram.Messenger.Handlers.LatestLayer.Stats";
    private const long ChannelId = 500_100;
    private const long CallerUserId = 42;

    private readonly PopulatedStatsService _statsService = new();
    private readonly StubAccessController _accessController = new(ChannelId);

    // ---- The seven handlers ----------------------------------------------------------------------------

    [Fact]
    public async Task GetBroadcastStatsHandler_returns_a_wellformed_serializable_broadcastStats()
    {
        var request = new RequestGetBroadcastStats { Channel = InputChannel(ChannelId), Dark = false };

        var result = await InvokeHandleCoreAsync("GetBroadcastStatsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TBroadcastStats>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task GetMegagroupStatsHandler_returns_a_wellformed_serializable_megagroupStats()
    {
        var request = new RequestGetMegagroupStats { Channel = InputChannel(ChannelId), Dark = true };

        var result = await InvokeHandleCoreAsync("GetMegagroupStatsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TMegagroupStats>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task GetMessageStatsHandler_returns_a_wellformed_serializable_messageStats()
    {
        var request = new RequestGetMessageStats { Channel = InputChannel(ChannelId), MsgId = 77, Dark = false };

        var result = await InvokeHandleCoreAsync("GetMessageStatsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TMessageStats>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task GetStoryStatsHandler_returns_a_wellformed_serializable_storyStats()
    {
        var request = new RequestGetStoryStats { Peer = InputPeerChannel(ChannelId), Id = 5, Dark = true };

        var result = await InvokeHandleCoreAsync("GetStoryStatsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TStoryStats>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task GetMessagePublicForwardsHandler_returns_a_wellformed_serializable_publicForwards()
    {
        var request = new RequestGetMessagePublicForwards
        {
            Channel = InputChannel(ChannelId),
            MsgId = 77,
            Offset = string.Empty,
            Limit = 20
        };

        var result = await InvokeHandleCoreAsync("GetMessagePublicForwardsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TPublicForwards>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task GetStoryPublicForwardsHandler_returns_a_wellformed_serializable_publicForwards()
    {
        var request = new RequestGetStoryPublicForwards
        {
            Peer = InputPeerChannel(ChannelId),
            Id = 5,
            Offset = string.Empty,
            Limit = 20
        };

        var result = await InvokeHandleCoreAsync("GetStoryPublicForwardsHandler", request, _accessController, _statsService);

        result.ShouldBeOfType<TPublicForwards>();
        AssertSerializes(result);
    }

    [Fact]
    public async Task LoadAsyncGraphHandler_returns_a_wellformed_serializable_statsGraph()
    {
        // LoadAsyncGraphHandler takes only the Stats_Service (the token itself scopes the request).
        var request = new RequestLoadAsyncGraph { Token = "token_1" };

        var result = await InvokeHandleCoreAsync("LoadAsyncGraphHandler", request, _statsService);

        result.ShouldBeAssignableTo<IStatsGraph>();
        result.ShouldBeOfType<TStatsGraph>();
        AssertSerializes(result);
    }

    // ---- Invocation + assertion helpers ----------------------------------------------------------------

    /// <summary>
    /// Constructs the internal handler <paramref name="handlerName"/> with <paramref name="ctorArgs"/> and
    /// invokes its <c>protected HandleCoreAsync(IRequestInput, TRequest)</c> core path via reflection,
    /// returning the produced schema result object.
    /// </summary>
    private static async Task<IObject> InvokeHandleCoreAsync(string handlerName, IObject request, params object[] ctorArgs)
    {
        var assembly = typeof(StatsService).Assembly;
        var handlerType = assembly.GetType($"{HandlerNamespace}.{handlerName}", throwOnError: true)!;

        var handler = Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ctorArgs,
            culture: null)!;

        var method = handlerType.GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"HandleCoreAsync not found on {handlerName}.");

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, new object[] { CreateInput(), request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        var result = (IObject)task.GetType().GetProperty("Result")!.GetValue(task)!;
        result.ShouldNotBeNull();
        return result;
    }

    /// <summary>
    /// Serializes the schema result object through the TL serialization API and asserts it produces a
    /// non-empty byte payload — i.e. every non-optional field was populated (Requirement 12.2).
    /// </summary>
    private static void AssertSerializes(IObject result)
    {
        var bytes = Should.NotThrow(() => result.ToBytes());
        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(0);
    }

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(CallerUserId);
        return input.Object;
    }

    private static IInputChannel InputChannel(long channelId) =>
        new TInputChannel { ChannelId = channelId, AccessHash = 0 };

    private static IInputPeer InputPeerChannel(long channelId) =>
        new TInputPeerChannel { ChannelId = channelId, AccessHash = 0 };

    /// <summary>
    /// Resolves the channel/peer successfully so each handler reaches its <see cref="IStatsService"/>
    /// delegation. The individual access-control failure branches are covered by Task 7.2/7.3.
    /// </summary>
    private sealed class StubAccessController(long channelId) : IStatsAccessController
    {
        public Task<IChannelReadModel> ResolveChannelForStatsAsync(IRequestInput input, IInputChannel channel,
            StatsChannelKind requiredKind, bool checkJoinable)
        {
            var readModel = new Mock<IChannelReadModel>(MockBehavior.Loose);
            readModel.SetupGet(x => x.ChannelId).Returns(channelId);
            return Task.FromResult(readModel.Object);
        }

        public Task<Peer> ResolvePeerForStoryStatsAsync(IRequestInput input, IInputPeer peer) =>
            Task.FromResult(new Peer(PeerType.Channel, channelId));
    }

    /// <summary>
    /// A faithful <see cref="IStatsService"/> stub that returns fully-populated, well-formed schema result
    /// objects for a representative fixture per method. Graph fields are produced by the real
    /// <see cref="GraphBuilder"/> so they are genuine <c>statsGraph</c> objects; every non-optional field of
    /// each result type is set so it serializes without a missing-field failure (Requirement 12.2).
    /// </summary>
    private sealed class PopulatedStatsService : IStatsService
    {
        private readonly IGraphBuilder _graphBuilder = new GraphBuilder(new FakeAsyncGraphStore());

        public async Task<IBroadcastStats> GetBroadcastStatsAsync(IRequestInput input, long channelId, bool dark)
        {
            var graph = await GraphAsync(dark);
            return new TBroadcastStats
            {
                Period = Range(),
                Followers = Abs(1200, 1100),
                ViewsPerPost = Abs(340, 300),
                SharesPerPost = Abs(45, 40),
                ReactionsPerPost = Abs(80, 70),
                ViewsPerStory = Abs(210, 190),
                SharesPerStory = Abs(15, 12),
                ReactionsPerStory = Abs(30, 25),
                EnabledNotifications = new TStatsPercentValue { Part = 900, Total = 1200 },
                GrowthGraph = graph,
                FollowersGraph = graph,
                MuteGraph = graph,
                TopHoursGraph = graph,
                InteractionsGraph = graph,
                IvInteractionsGraph = graph,
                ViewsBySourceGraph = graph,
                NewFollowersBySourceGraph = graph,
                LanguagesGraph = graph,
                ReactionsByEmotionGraph = graph,
                StoryInteractionsGraph = graph,
                StoryReactionsByEmotionGraph = graph,
                RecentPostsInteractions = new TVector<IPostInteractionCounters>(new IPostInteractionCounters[]
                {
                    new TPostInteractionCountersMessage { MsgId = 77, Views = 340, Forwards = 45, Reactions = 80 },
                    new TPostInteractionCountersStory { StoryId = 5, Views = 210, Forwards = 15, Reactions = 30 }
                })
            };
        }

        public async Task<IMegagroupStats> GetMegagroupStatsAsync(IRequestInput input, long channelId, bool dark)
        {
            var graph = await GraphAsync(dark);
            return new TMegagroupStats
            {
                Period = Range(),
                Members = Abs(500, 480),
                Messages = Abs(3200, 3000),
                Viewers = Abs(410, 400),
                Posters = Abs(120, 115),
                GrowthGraph = graph,
                MembersGraph = graph,
                NewMembersBySourceGraph = graph,
                LanguagesGraph = graph,
                MessagesGraph = graph,
                ActionsGraph = graph,
                TopHoursGraph = graph,
                WeekdaysGraph = graph,
                TopPosters = new TVector<IStatsGroupTopPoster>(new IStatsGroupTopPoster[]
                {
                    new TStatsGroupTopPoster { UserId = 7, Messages = 100, AvgChars = 42 }
                }),
                TopAdmins = new TVector<IStatsGroupTopAdmin>(new IStatsGroupTopAdmin[]
                {
                    new TStatsGroupTopAdmin { UserId = 8, Deleted = 1, Kicked = 2, Banned = 0 }
                }),
                TopInviters = new TVector<IStatsGroupTopInviter>(new IStatsGroupTopInviter[]
                {
                    new TStatsGroupTopInviter { UserId = 9, Invitations = 5 }
                }),
                Users = new TVector<IUser>(new IUser[]
                {
                    new TUser { Id = 7 }, new TUser { Id = 8 }, new TUser { Id = 9 }
                })
            };
        }

        public async Task<IMessageStats> GetMessageStatsAsync(IRequestInput input, long channelId, int msgId, bool dark)
        {
            var graph = await GraphAsync(dark);
            return new TMessageStats { ViewsGraph = graph, ReactionsByEmotionGraph = graph };
        }

        public async Task<IStoryStats> GetStoryStatsAsync(IRequestInput input, Peer peer, int storyId, bool dark)
        {
            var graph = await GraphAsync(dark);
            return new TStoryStats { ViewsGraph = graph, ReactionsByEmotionGraph = graph };
        }

        public Task<IPublicForwards> GetMessagePublicForwardsAsync(IRequestInput input, long channelId, int msgId, string offset, int limit) =>
            Task.FromResult(PublicForwards());

        public Task<IPublicForwards> GetStoryPublicForwardsAsync(IRequestInput input, Peer peer, int storyId, string offset, int limit) =>
            Task.FromResult(PublicForwards());

        public Task<IStatsGraph> LoadAsyncGraphAsync(IRequestInput input, string token, long? x) =>
            GraphAsync(dark: false);

        // -- fixture builders --

        private Task<IStatsGraph> GraphAsync(bool dark)
        {
            var spec = new GraphSpec(
                GraphKind.Line,
                new long[] { 1_690_848_000_000L, 1_690_934_400_000L, 1_691_020_800_000L },
                new[] { new GraphSeries("y0", "Views", "primary", new long[] { 12, 15, 9 }) });
            return _graphBuilder.BuildInlineAsync(spec, dark, "snapshot:smoke", 1_691_020_800);
        }

        private static IPublicForwards PublicForwards() =>
            new TPublicForwards
            {
                Count = 2,
                Forwards = new TVector<IPublicForward>(new IPublicForward[]
                {
                    new TPublicForwardStory
                    {
                        Peer = new TPeerChannel { ChannelId = 555_222 },
                        Story = new TStoryItemDeleted { Id = 5 }
                    }
                }),
                NextOffset = "cursor_10",
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>(new IUser[] { new TUser { Id = 7 } })
            };

        private static IStatsDateRangeDays Range() =>
            new TStatsDateRangeDays { MinDate = 1_690_848_000, MaxDate = 1_691_020_800 };

        private static IStatsAbsValueAndPrev Abs(long current, long previous) =>
            new TStatsAbsValueAndPrev { Current = current, Previous = previous };
    }
}
