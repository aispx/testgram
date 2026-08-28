using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.TopPeers;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.TopPeers;

/// <summary>
/// Feature: the <a href="https://corefork.telegram.org/api/top-rating">top peer rating</a> that
/// pre-populates the chats tab of global search, the "@" inline strip and the mini-app strip.
///
/// <para>
/// Two sources feed it. Correspondents, private bots, groups and channels are derived from the
/// caller's outgoing messages; inline picks, mini-app opens, calls and forwards leave no usable trace
/// there and are counted from explicitly recorded uses. Both use the same
/// <c>Σ exp((date - now) / rating_e_decay)</c>, because clients add their own increments in that scale
/// on top of whatever the server sent.
/// </para>
/// <para>
/// These run against a real <c>mongod</c>: the ranking is an aggregation, and enums are stored as their
/// numeric value — a string comparison there silently matches nothing.
/// </para>
/// </summary>
public class TopPeerRatingServiceTests
{
    private const long SelfUserId = 777;
    private const string MessageCollection = "eventflow-messagereadmodel";
    private const string BotStateCollection = "botfather-bot-state";

    private static readonly TopPeerCategory[] AllCategories = TopPeerCategoryHelper.WireOrder;

    [RequiresMongoDbFact]
    public async Task Every_requested_category_is_answered_even_when_empty()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var ratings = await CreateService(mongo.Database).GetRatingsAsync(SelfUserId, AllCategories, Now());

        // tdlib clears its cached copy only for the categories present in the answer, so an omitted one
        // keeps stale peers in the vector it hashes and topPeersNotModified stops matching for good.
        ratings.Keys.ShouldBe(AllCategories, ignoreOrder: true);
        ratings.Values.ShouldAllBe(p => p.Count == 0);
    }

    [RequiresMongoDbFact]
    public async Task Peers_are_ranked_by_how_often_we_message_them()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 2, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 9, date: now - 60);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10), User(20));

        correspondents.Select(p => p.PeerId).ShouldBe([20, 10]);
    }

    [RequiresMongoDbFact]
    public async Task A_recent_conversation_outranks_an_equally_busy_old_one()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 5, date: now - 60 * 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 5, date: now - 60 * 24 * 60 * 60);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10), User(20));

        correspondents[0].PeerId.ShouldBe(10);
        correspondents[0].Rating.ShouldBeGreaterThan(correspondents[1].Rating);
    }

    [RequiresMongoDbFact]
    public async Task The_rating_is_the_sum_of_the_increments_a_client_would_add()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();
        var age = 7 * 24 * 60 * 60;

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 3, date: now - age);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10));

        // tdlib rating_add / Android Math.exp(dt / ratingDecay), once per use — not a count scaled by a
        // single decay, and not a decay of our own choosing.
        var expected = 3 * Math.Exp(-(double)age / TopPeerRatingConstants.RatingEDecaySeconds);
        correspondents.Single().Rating.ShouldBe(expected, 1e-9);
    }

    [RequiresMongoDbFact]
    public async Task Incoming_messages_and_other_peoples_chats_do_not_count()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 1, date: now - 60);
        // Received, not sent.
        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 11, date: now - 60, isOut: false);
        // Someone else's dialog.
        await AddMessageAsync(mongo.Database, ownerPeerId: 999, peerId: 12, date: now - 60, isOut: true);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10), User(11), User(12));

        correspondents.Select(p => p.PeerId).ShouldBe([10]);
    }

    [RequiresMongoDbFact]
    public async Task Messages_older_than_the_rating_window_are_ignored()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 3, date: now - 200 * 24 * 60 * 60);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10));

        correspondents.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Bots_are_ranked_separately_from_correspondents()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 1, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 1, date: now - 60);

        var ratings = await CreateService(mongo.Database, [User(10), User(20, bot: true)])
            .GetRatingsAsync(SelfUserId, AllCategories, now);

        ratings[TopPeerCategory.Correspondents].Select(p => p.PeerId).ShouldBe([10]);
        ratings[TopPeerCategory.BotsPM].Select(p => p.PeerId).ShouldBe([20]);
    }

    [RequiresMongoDbFact]
    public async Task Saved_messages_and_deleted_accounts_are_not_correspondents()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddMessageAsync(mongo.Database, SelfUserId, SelfUserId, now - 60, isOut: true,
            peerType: PeerType.Self);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 1, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 30, count: 1, date: now - 60);

        var correspondents = await CorrespondentsAsync(mongo.Database, now,
            User(SelfUserId), User(20, deleted: true), User(30));

        // Android drops the self peer on both read and write, and a deleted account is a row a client
        // cannot draw.
        correspondents.Select(p => p.PeerId).ShouldBe([30]);
    }

    [RequiresMongoDbFact]
    public async Task Megagroups_are_groups_and_broadcasts_are_channels()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 100, date: now - 60, isOut: true,
            peerType: PeerType.Channel);
        await AddMessageAsync(mongo.Database, SelfUserId, peerId: 200, date: now - 60, isOut: true,
            peerType: PeerType.Channel);

        var ratings = await CreateService(mongo.Database, channels:
                [Channel(100, megagroup: true), Channel(200, broadcast: true)])
            .GetRatingsAsync(SelfUserId, AllCategories, now);

        ratings[TopPeerCategory.Groups].Select(p => p.PeerId).ShouldBe([100]);
        ratings[TopPeerCategory.Channels].Select(p => p.PeerId).ShouldBe([200]);
    }

    [RequiresMongoDbFact]
    public async Task Top_peers_can_be_disabled_and_re_enabled()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);

        (await service.IsDisabledAsync(SelfUserId)).ShouldBeFalse();

        await service.SetDisabledAsync(SelfUserId, true);
        (await service.IsDisabledAsync(SelfUserId)).ShouldBeTrue();

        await service.SetDisabledAsync(SelfUserId, false);
        (await service.IsDisabledAsync(SelfUserId)).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task Recorded_uses_rank_the_categories_no_message_expresses()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();
        var usage = new TopPeerUsageStore(mongo.Database);

        await usage.RecordAsync(SelfUserId, TopPeerCategory.PhoneCalls, PeerType.User, 10, now - 60);
        await usage.RecordAsync(SelfUserId, TopPeerCategory.PhoneCalls, PeerType.User, 20, now - 60);
        await usage.RecordAsync(SelfUserId, TopPeerCategory.PhoneCalls, PeerType.User, 20, now - 120);
        await usage.RecordAsync(SelfUserId, TopPeerCategory.ForwardChats, PeerType.Channel, 100, now - 60);

        var ratings = await CreateService(mongo.Database).GetRatingsAsync(SelfUserId, AllCategories, now);

        // Two calls outrank one — the rating inside these categories is the count of the thing the
        // category is about, not the count of messages exchanged with the peer.
        ratings[TopPeerCategory.PhoneCalls].Select(p => p.PeerId).ShouldBe([20, 10]);
        ratings[TopPeerCategory.ForwardChats].Select(p => p.PeerId).ShouldBe([100]);
        ratings[TopPeerCategory.ForwardUsers].ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task A_bot_without_inline_mode_never_reaches_the_inline_category()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();
        var usage = new TopPeerUsageStore(mongo.Database);

        await usage.RecordAsync(SelfUserId, TopPeerCategory.BotsInline, PeerType.User, 10, now - 60);
        await usage.RecordAsync(SelfUserId, TopPeerCategory.BotsInline, PeerType.User, 20, now - 60);
        await AddBotStateAsync(mongo.Database, botUserId: 10, inlineEnabled: true);
        await AddBotStateAsync(mongo.Database, botUserId: 20, inlineEnabled: false);

        var ratings = await CreateService(mongo.Database).GetRatingsAsync(SelfUserId, AllCategories, now);

        // Android feeds this category straight into the "@" suggestion strip, so a bot that answers no
        // inline query there is a suggestion that cannot work.
        ratings[TopPeerCategory.BotsInline].Select(p => p.PeerId).ShouldBe([10]);
    }

    [RequiresMongoDbFact]
    public async Task Resetting_one_category_leaves_the_others_alone()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 4, date: now - 60);
        await new TopPeerUsageStore(mongo.Database)
            .RecordAsync(SelfUserId, TopPeerCategory.BotsInline, PeerType.User, 10, now - 60);
        await AddBotStateAsync(mongo.Database, botUserId: 10, inlineEnabled: true);

        await CreateService(mongo.Database)
            .ResetAsync(SelfUserId, TopPeerCategory.BotsInline, PeerType.User, 10);

        var ratings = await CreateService(mongo.Database, [User(10, bot: true)])
            .GetRatingsAsync(SelfUserId, AllCategories, now);

        // Dismissing a bot from the inline strip must not erase it from the frequently-messaged row:
        // Android, iOS and telegram-tt all send one category at a time.
        ratings[TopPeerCategory.BotsInline].ShouldBeEmpty();
        ratings[TopPeerCategory.BotsPM].Select(p => p.PeerId).ShouldBe([10]);
    }

    [RequiresMongoDbFact]
    public async Task Resetting_a_message_derived_category_is_remembered()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 4, date: now - 60);
        await AddOutgoingMessagesAsync(mongo.Database, peerId: 20, count: 1, date: now - 60);

        await CreateService(mongo.Database)
            .ResetAsync(SelfUserId, TopPeerCategory.Correspondents, PeerType.User, 10);

        var correspondents = await CorrespondentsAsync(mongo.Database, now, User(10), User(20));

        // The messages are still there, so a reset that was not remembered would be undone by the next
        // refresh.
        correspondents.Select(p => p.PeerId).ShouldBe([20]);
    }

    [RequiresMongoDbFact]
    public async Task Resetting_a_recorded_category_lets_the_peer_climb_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();
        var usage = new TopPeerUsageStore(mongo.Database);

        await usage.RecordAsync(SelfUserId, TopPeerCategory.BotsApp, PeerType.User, 10, now - 600);
        await CreateService(mongo.Database).ResetAsync(SelfUserId, TopPeerCategory.BotsApp, PeerType.User, 10);

        (await CreateService(mongo.Database).GetRatingsAsync(SelfUserId, AllCategories, now))
            [TopPeerCategory.BotsApp].ShouldBeEmpty();

        // Nothing masks the peer, so using the mini app again ranks it again — which is what "reset the
        // rating" means for a counter the server owns outright.
        await usage.RecordAsync(SelfUserId, TopPeerCategory.BotsApp, PeerType.User, 10, now - 60);

        (await CreateService(mongo.Database).GetRatingsAsync(SelfUserId, AllCategories, now))
            [TopPeerCategory.BotsApp].Select(p => p.PeerId).ShouldBe([10]);
    }

    [RequiresMongoDbFact]
    public async Task A_legacy_exclusion_row_still_hides_the_peer_everywhere()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = Now();

        await AddOutgoingMessagesAsync(mongo.Database, peerId: 10, count: 4, date: now - 60);
        await new TopPeerUsageStore(mongo.Database)
            .RecordAsync(SelfUserId, TopPeerCategory.PhoneCalls, PeerType.User, 10, now - 60);

        // The shape resetTopPeerRating wrote before it honoured the category: no Category field.
        await mongo.Database.GetCollection<BsonDocument>("top_peers_excluded").InsertOneAsync(new BsonDocument
        {
            { "_id", $"{SelfUserId}-User-10" },
            { "UserId", SelfUserId },
            { "PeerType", (int)PeerType.User },
            { "PeerId", 10L }
        });

        var ratings = await CreateService(mongo.Database, [User(10)])
            .GetRatingsAsync(SelfUserId, AllCategories, now);

        ratings.Values.ShouldAllBe(p => p.Count == 0);
    }

    private static async Task<List<TopPeerRating>> CorrespondentsAsync(IMongoDatabase database, int now,
        params IUserReadModel[] users)
    {
        var ratings = await CreateService(database, users).GetRatingsAsync(SelfUserId, AllCategories, now);

        return ratings[TopPeerCategory.Correspondents];
    }

    private static ITopPeerRatingService CreateService(IMongoDatabase database,
        IReadOnlyCollection<IUserReadModel>? users = null,
        IReadOnlyCollection<IChannelReadModel>? channels = null)
    {
        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetListAsync(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync((IEnumerable<long> ids) => Filter(users, ids, p => p.UserId));

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetListAsync(It.IsAny<IEnumerable<long>>()))
            .ReturnsAsync((IEnumerable<long> ids) => Filter(channels, ids, p => p.ChannelId));

        // A fresh cache per service: the real one is a singleton with a 60 s TTL, and a test that seeds
        // and then reads would otherwise be answered from the snapshot taken before it seeded.
        return new TopPeerRatingService(database, userAppService.Object, channelAppService.Object,
            new TopPeerSettingsStore(database), new TopPeerUsageStore(database), new TopPeerRatingCache());
    }

    private static IReadOnlyCollection<T> Filter<T>(IReadOnlyCollection<T>? items, IEnumerable<long> ids,
        Func<T, long> keySelector)
    {
        var wanted = ids.ToHashSet();

        return (items ?? []).Where(p => wanted.Contains(keySelector(p))).ToList();
    }

    private static IUserReadModel User(long userId, bool bot = false, bool deleted = false)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(userId);
        user.SetupGet(p => p.Bot).Returns(bot);
        user.SetupGet(p => p.IsDeleted).Returns(deleted);

        return user.Object;
    }

    private static IChannelReadModel Channel(long channelId, bool megagroup = false, bool broadcast = false)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.ChannelId).Returns(channelId);
        channel.SetupGet(p => p.MegaGroup).Returns(megagroup);
        channel.SetupGet(p => p.Broadcast).Returns(broadcast);

        return channel.Object;
    }

    private static int Now()
    {
        return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static Task AddBotStateAsync(IMongoDatabase database, long botUserId, bool inlineEnabled)
    {
        return database.GetCollection<BsonDocument>(BotStateCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "BotUserId", botUserId },
            { "InlineEnabled", inlineEnabled }
        });
    }

    private static async Task AddOutgoingMessagesAsync(IMongoDatabase database, long peerId, int count, int date)
    {
        for (var i = 0; i < count; i++)
        {
            await AddMessageAsync(database, SelfUserId, peerId, date, isOut: true);
        }
    }

    private static Task AddMessageAsync(IMongoDatabase database, long ownerPeerId, long peerId, int date, bool isOut,
        PeerType peerType = PeerType.User)
    {
        return database.GetCollection<BsonDocument>(MessageCollection).InsertOneAsync(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "OwnerPeerId", ownerPeerId },
            { "Out", isOut },
            { "Date", date },
            // Enums are persisted as their numeric value, not as their name.
            { "ToPeerType", (int)peerType },
            { "ToPeerId", peerId }
        });
    }
}
