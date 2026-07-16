using EventFlow.Queries;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Stats;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 8.7 — example/edge-case unit tests for the <see cref="StatsService"/> error and
/// zero branches. These complement the Stats_Service property tasks (Properties 3, 9) by pinning down the
/// exact single-branch outcomes:
/// <list type="bullet">
///   <item>A <c>msg_id</c> that does not identify an existing post → <c>MESSAGE_ID_INVALID</c>
///   (Requirements 4.2 for <see cref="StatsService.GetMessageStatsAsync"/>, 6.5 for
///   <see cref="StatsService.GetMessagePublicForwardsAsync"/>).</item>
///   <item>A peer that has never posted a story → <c>STORIES_NEVER_CREATED</c> (Requirements 5.2, 7.6).</item>
///   <item>A peer that has posted stories but not the requested id → <c>PEER_ID_INVALID</c>
///   (Requirements 5.3, 7.7 — and, for a resolved peer, 7.5).</item>
///   <item>A channel/supergroup with no recorded metric → every <c>statsAbsValueAndPrev</c> field has
///   <c>current</c> = <c>previous</c> = 0 (Requirements 2.8, 3.7).</item>
/// </list>
///
/// <para>The message/zero branches use pure fakes: an <see cref="EmptyMetricsStore"/> that faithfully
/// mirrors the documented Metrics_Store "no recorded metric" semantics (<c>{0,0}</c> period, <c>0</c>
/// aggregates, empty series/recent/top lists), the real <see cref="GraphBuilder"/> over the shared
/// <see cref="FakeAsyncGraphStore"/>, and Moq for the collaborators. The <see cref="IQueryProcessor"/> is
/// stubbed to return <see langword="null"/> for the message existence query so the
/// <c>MESSAGE_ID_INVALID</c> branch is reached.</para>
///
/// <para>The story-existence branches query <c>IMongoDatabase.GetCollection&lt;StoryDocument&gt;("stories")</c>
/// directly, which cannot be faked without a real MongoDB, so those tests run against
/// <see cref="EmbeddedMongoServer"/> under <see cref="RequiresMongoDbFactAttribute"/> (skipped cleanly when
/// no <c>mongod</c> is available), following the same harness the ingestion integration test uses.</para>
/// </summary>
public class StatsServiceErrorBranchTests
{
    private const long CallerUserId = 42;
    private const long ChannelId = 500_100;
    private const int MsgId = 77;

    private readonly Mock<IMetricsStore> _metricsStore = new(MockBehavior.Loose);
    private readonly Mock<IGraphBuilder> _graphBuilder = new(MockBehavior.Loose);
    private readonly Mock<IUserConverterService> _userConverter = new(MockBehavior.Loose);
    private readonly Mock<IChatConverterService> _chatConverter = new(MockBehavior.Loose);
    private readonly Mock<IPublicForwardStore> _publicForwardStore = new(MockBehavior.Loose);
    private readonly Mock<IAsyncGraphStore> _asyncGraphStore = new(MockBehavior.Loose);
    private readonly Mock<IMessageConverterService> _messageConverter = new(MockBehavior.Loose);
    private readonly Mock<IMessageAppService> _messageAppService = new(MockBehavior.Loose);
    private readonly Mock<IQueryProcessor> _queryProcessor = new(MockBehavior.Loose);
    private readonly Mock<IMongoDatabase> _mongoDatabase = new(MockBehavior.Loose);

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(CallerUserId);
        return input.Object;
    }

    private StatsService CreateService(IMetricsStore? metricsStore = null, IMongoDatabase? mongoDatabase = null) =>
        new(
            metricsStore ?? _metricsStore.Object,
            _graphBuilder.Object,
            _userConverter.Object,
            _chatConverter.Object,
            _publicForwardStore.Object,
            _asyncGraphStore.Object,
            _messageConverter.Object,
            _messageAppService.Object,
            _queryProcessor.Object,
            mongoDatabase ?? _mongoDatabase.Object,
            StatsTestOptions.Create());

    /// <summary>
    /// Stubs the message existence query (<see cref="GetMessageByPeerIdAndMessageIdQuery"/>) to return the
    /// supplied read model (or <see langword="null"/> to simulate a non-existent post).
    /// </summary>
    private void StubMessageLookup(IMessageReadModel? readModel)
    {
        _queryProcessor
            .Setup(x => x.ProcessAsync(It.IsAny<GetMessageByPeerIdAndMessageIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(readModel);
    }

    // ---- MESSAGE_ID_INVALID (Requirements 4.2, 6.5) -----------------------------------------------------

    [Fact]
    public async Task GetMessageStats_with_nonexistent_message_throws_MESSAGE_ID_INVALID()
    {
        // The msg_id does not identify an existing post: the existence query resolves to null.
        StubMessageLookup(null);
        var service = CreateService();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetMessageStatsAsync(CreateInput(), ChannelId, MsgId, dark: false));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("MESSAGE_ID_INVALID");
    }

    [Fact]
    public async Task GetMessagePublicForwards_with_nonexistent_message_throws_MESSAGE_ID_INVALID()
    {
        StubMessageLookup(null);
        var service = CreateService();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetMessagePublicForwardsAsync(CreateInput(), ChannelId, MsgId, offset: "", limit: 20));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("MESSAGE_ID_INVALID");

        // The invalid-message branch must reject before ever reading a page from the store (Requirement 6.5).
        _publicForwardStore.Verify(
            x => x.GetPageAsync(It.IsAny<ForwardSourceKey>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    // ---- Missing metric → current/previous = 0 (Requirements 2.8, 3.7) ----------------------------------

    [Fact]
    public async Task GetBroadcastStats_with_no_recorded_metric_returns_zero_abs_values()
    {
        // A real GraphBuilder (over the shared FakeAsyncGraphStore) so every graph field serializes; an
        // empty Metrics_Store so every statsAbsValueAndPrev aggregate and the notify counts are 0.
        var graphBuilder = new GraphBuilder(new FakeAsyncGraphStore());
        var service = new StatsService(
            new EmptyMetricsStore(),
            graphBuilder,
            _userConverter.Object,
            _chatConverter.Object,
            _publicForwardStore.Object,
            _asyncGraphStore.Object,
            _messageConverter.Object,
            _messageAppService.Object,
            _queryProcessor.Object,
            _mongoDatabase.Object,
            StatsTestOptions.Create());

        var result = await service.GetBroadcastStatsAsync(CreateInput(), ChannelId, dark: false);

        var stats = result.ShouldBeOfType<TBroadcastStats>();

        // Every statsAbsValueAndPrev field is {current=0, previous=0} when no metric is recorded (2.8).
        ShouldBeZero(stats.Followers);
        ShouldBeZero(stats.ViewsPerPost);
        ShouldBeZero(stats.SharesPerPost);
        ShouldBeZero(stats.ReactionsPerPost);
        ShouldBeZero(stats.ViewsPerStory);
        ShouldBeZero(stats.SharesPerStory);
        ShouldBeZero(stats.ReactionsPerStory);

        // enabled_notifications derives from the (also-zero) notify counts.
        stats.EnabledNotifications.Part.ShouldBe(0);
        stats.EnabledNotifications.Total.ShouldBe(0);

        // The empty period is reported as {0,0} (Requirement 10.4) and the recent-posts list is empty.
        stats.Period.MinDate.ShouldBe(0);
        stats.Period.MaxDate.ShouldBe(0);
        stats.RecentPostsInteractions.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetMegagroupStats_with_no_recorded_metric_returns_zero_abs_values()
    {
        var graphBuilder = new GraphBuilder(new FakeAsyncGraphStore());
        var service = new StatsService(
            new EmptyMetricsStore(),
            graphBuilder,
            _userConverter.Object,
            _chatConverter.Object,
            _publicForwardStore.Object,
            _asyncGraphStore.Object,
            _messageConverter.Object,
            _messageAppService.Object,
            _queryProcessor.Object,
            _mongoDatabase.Object,
            StatsTestOptions.Create());

        var result = await service.GetMegagroupStatsAsync(CreateInput(), ChannelId, dark: false);

        var stats = result.ShouldBeOfType<TMegagroupStats>();

        // Every statsAbsValueAndPrev field (members/messages/viewers/posters) is {0,0} (Requirement 3.7).
        ShouldBeZero(stats.Members);
        ShouldBeZero(stats.Messages);
        ShouldBeZero(stats.Viewers);
        ShouldBeZero(stats.Posters);

        // No top entities means no referenced users.
        stats.TopPosters.Count.ShouldBe(0);
        stats.TopAdmins.Count.ShouldBe(0);
        stats.TopInviters.Count.ShouldBe(0);
        stats.Users.Count.ShouldBe(0);
    }

    private static void ShouldBeZero(IStatsAbsValueAndPrev value)
    {
        value.Current.ShouldBe(0);
        value.Previous.ShouldBe(0);
    }

    // ---- Story existence branches (Requirements 5.2, 5.3, 7.6, 7.7) -------------------------------------

    [RequiresMongoDbFact]
    public async Task GetStoryStats_when_peer_never_posted_a_story_throws_STORIES_NEVER_CREATED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        // No stories inserted for this peer → the peer has never posted a story (Requirement 5.2).
        var service = CreateService(mongoDatabase: mongo.Database);
        var peer = new Peer(PeerType.User, 900_001);

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetStoryStatsAsync(CreateInput(), peer, storyId: 1, dark: false));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("STORIES_NEVER_CREATED");
    }

    [RequiresMongoDbFact]
    public async Task GetStoryStats_when_story_id_does_not_exist_throws_PEER_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var peer = new Peer(PeerType.User, 900_002);
        // The peer has posted a story (id 10), but a different id (99) is requested (Requirement 5.3).
        await InsertStoryAsync(mongo.Database, peer, storyId: 10);
        var service = CreateService(mongoDatabase: mongo.Database);

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetStoryStatsAsync(CreateInput(), peer, storyId: 99, dark: false));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task GetStoryPublicForwards_when_peer_never_posted_a_story_throws_STORIES_NEVER_CREATED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongoDatabase: mongo.Database);
        var peer = new Peer(PeerType.User, 900_003);

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetStoryPublicForwardsAsync(CreateInput(), peer, storyId: 1, offset: "", limit: 20));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("STORIES_NEVER_CREATED");
    }

    [RequiresMongoDbFact]
    public async Task GetStoryPublicForwards_when_story_id_does_not_exist_throws_PEER_ID_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var peer = new Peer(PeerType.User, 900_004);
        await InsertStoryAsync(mongo.Database, peer, storyId: 5);
        var service = CreateService(mongoDatabase: mongo.Database);

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetStoryPublicForwardsAsync(CreateInput(), peer, storyId: 42, offset: "", limit: 20));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task GetStoryStats_ignores_deleted_stories_when_deciding_existence()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var peer = new Peer(PeerType.User, 900_005);
        // Only a soft-deleted story exists; the existence check filters Deleted == false, so the peer is
        // treated as having never posted a (live) story → STORIES_NEVER_CREATED.
        await InsertStoryAsync(mongo.Database, peer, storyId: 7, deleted: true);
        var service = CreateService(mongoDatabase: mongo.Database);

        var ex = await Should.ThrowAsync<RpcException>(() =>
            service.GetStoryStatsAsync(CreateInput(), peer, storyId: 7, dark: false));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("STORIES_NEVER_CREATED");
    }

    private static async Task InsertStoryAsync(IMongoDatabase database, Peer peer, int storyId, bool deleted = false)
    {
        var stories = database.GetCollection<StoryDocument>("stories");
        await stories.InsertOneAsync(new StoryDocument
        {
            OwnerPeerId = peer.PeerId,
            OwnerPeerType = StoryHelper.ToStoryPeerType(peer.PeerType),
            StoryId = storyId,
            Date = 1_690_848_000,
            Deleted = deleted
        });
    }

    /// <summary>
    /// An <see cref="IMetricsStore"/> holding no data: it mirrors the documented Metrics_Store semantics for
    /// an entity with no recorded metric — <c>GetPeriodAsync</c> returns <c>{0,0}</c> (Requirement 10.4),
    /// <c>AggregateAsync</c> returns <c>0</c> for any range (Requirements 2.8, 3.7), and the series/recent/
    /// top-entity reads return empty results.
    /// </summary>
    private sealed class EmptyMetricsStore : IMetricsStore
    {
        public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
            IReadOnlyDictionary<string, long>? breakdown = null) => Task.CompletedTask;

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays) =>
            Task.FromResult(new StatsDateRange(0, 0));

        public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            Task.FromResult<IReadOnlyList<DailyPoint>>([]);

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            Task.FromResult<IReadOnlyList<CategorySeries>>([]);

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            Task.FromResult<IReadOnlyList<PostInteraction>>([]);

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            Task.FromResult(new TopEntities([], [], [], []));
    }
}
