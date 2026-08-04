using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Caching.Redis;
using MyTelegram.Core;
using MyTelegram.Messenger.Services.Impl;
using System.Text.Json;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Recommend;

/// <summary>
/// Integration tests for <see cref="RecommendationAppService"/> — the subscriber-base overlap engine behind
/// <c>channels.getChannelRecommendations</c> and <c>bots.getBotRecommendations</c>
/// (see https://corefork.telegram.org/api/recommend).
///
/// <para>The service issues raw MongoDB aggregation pipelines against the read-model collections, so these
/// run against a real <c>mongod</c> via <see cref="EmbeddedMongoServer"/> rather than a mock — a mocked
/// driver would not validate the pipelines themselves, which is the whole of the logic under test.</para>
/// </summary>
public class RecommendationAppServiceTests
{
    private const int PeerTypeUser = (int)PeerType.User;

    // ---- channels ------------------------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Similar_channels_are_ordered_by_shared_subscriber_count()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        // Source channel 100 has subscribers 1..4.
        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3, 4]);
        // 201 shares 3 of them, 202 shares 2, 203 shares 1 → that exact order is expected.
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 202, userIds: [1, 2]);
        await AddMembersAsync(db, channelId: 203, userIds: [4]);
        await AddPublicBroadcastChannelsAsync(db, 100, 201, 202, 203);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        result.Ids.ShouldBe([201, 202, 203]);
        result.TotalCount.ShouldBe(3);
    }

    [RequiresMongoDbFact]
    public async Task Source_channel_is_never_recommended_to_itself()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        result.Ids.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Channels_the_caller_already_joined_are_excluded()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 202, userIds: [1, 2, 3]);
        // The caller is a member of 202, so only 201 is actionable.
        await AddMembersAsync(db, channelId: 202, userIds: [99]);
        await AddPublicBroadcastChannelsAsync(db, 100, 201, 202);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        result.Ids.ShouldBe([201]);
    }

    [RequiresMongoDbFact]
    public async Task Private_groups_and_deleted_channels_are_not_recommended()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]); // public broadcast — keep
        await AddMembersAsync(db, channelId: 202, userIds: [1, 2, 3]); // no username — drop
        await AddMembersAsync(db, channelId: 203, userIds: [1, 2, 3]); // megagroup — drop
        await AddMembersAsync(db, channelId: 204, userIds: [1, 2, 3]); // deleted — drop
        await AddPublicBroadcastChannelsAsync(db, 100, 201);
        await AddChannelAsync(db, 202, broadcast: true, userName: "", isDeleted: false);
        await AddChannelAsync(db, 203, broadcast: false, userName: "group", isDeleted: false);
        await AddChannelAsync(db, 204, broadcast: true, userName: "gone", isDeleted: true);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        result.Ids.ShouldBe([201]);
    }

    [RequiresMongoDbFact]
    public async Task Left_and_kicked_members_do_not_contribute_to_overlap()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMemberAsync(db, channelId: 201, userId: 1, left: true);
        await AddMemberAsync(db, channelId: 201, userId: 2, kicked: true);
        await AddMemberAsync(db, channelId: 201, userId: 3, isBot: false);
        await AddPublicBroadcastChannelsAsync(db, 100, 201);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        // Only the single active membership counts, but the channel still qualifies.
        result.Ids.ShouldBe([201]);
    }

    [RequiresMongoDbFact]
    public async Task Total_count_reports_all_matches_while_ids_honour_max()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100);
        for (long channelId = 201; channelId <= 205; channelId++)
        {
            await AddMembersAsync(db, channelId, userIds: [1, 2, 3]);
            await AddPublicBroadcastChannelsAsync(db, channelId);
        }

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 2, totalCap: 100);

        // This is what lets the handler return chatsSlice with a truthful count for non-premium callers.
        result.Ids.Count.ShouldBe(2);
        result.TotalCount.ShouldBe(5);
    }

    [RequiresMongoDbFact]
    public async Task Total_count_never_exceeds_the_cap_a_premium_account_would_receive()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100);
        for (long channelId = 201; channelId <= 210; channelId++)
        {
            await AddMembersAsync(db, channelId, userIds: [1, 2, 3]);
            await AddPublicBroadcastChannelsAsync(db, channelId);
        }

        IRecommendationAppService service = CreateService(db);
        // 10 candidates exist, but a premium account is only ever served 4 of them.
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 2, totalCap: 4);

        // Clients render "count - ids.Count" as "unlock N more with Premium", so a count of 10 would
        // advertise six channels nobody could ever obtain.
        result.Ids.Count.ShouldBe(2);
        result.TotalCount.ShouldBe(4);
    }

    [RequiresMongoDbFact]
    public async Task Every_source_channel_contributes_to_the_audience_sample()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        // The caller is in two channels. The first is far larger, and would swallow a shared sample
        // budget entirely — the second must still contribute its own subscribers.
        await AddMembersAsync(db, channelId: 100, userIds: [.. Enumerable.Range(1000, 400).Select(p => (long)p)]);
        await AddMemberAsync(db, channelId: 100, userId: 99);
        await AddMembersAsync(db, channelId: 101, userIds: [1, 2, 3]);
        await AddMemberAsync(db, channelId: 101, userId: 99);
        // 201 only overlaps with the small channel's subscribers.
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100, 101, 201);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: null, max: 10, totalCap: 100);

        result.Ids.ShouldContain(201);
    }

    [RequiresMongoDbFact]
    public async Task Candidates_are_computed_once_and_then_served_from_cache()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100, 201);

        var cache = new FakeCacheManager<RecommendationCacheItem>();
        IRecommendationAppService service = CreateService(db, cache);

        var first = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);
        var second = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);

        first.Ids.ShouldBe([201]);
        second.Ids.ShouldBe(first.Ids);
        // Clients hit this on every channel join and profile open, so the aggregation must not re-run.
        cache.SetCount.ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Cached_candidates_are_still_filtered_per_caller()    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 202, userIds: [1, 2, 3]);
        // Caller 98 is already in 202; caller 99 is not. One shared cache entry must serve both.
        await AddMemberAsync(db, channelId: 202, userId: 98);
        await AddPublicBroadcastChannelsAsync(db, 100, 201, 202);

        var cache = new FakeCacheManager<RecommendationCacheItem>();
        IRecommendationAppService service = CreateService(db, cache);

        var forNinetyNine = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: 100, max: 10, totalCap: 100);
        var forNinetyEight = await service.GetSimilarChannelIdsAsync(selfUserId: 98, sourceChannelId: 100, max: 10, totalCap: 100);

        forNinetyNine.Ids.ShouldBe([201, 202]);
        forNinetyEight.Ids.ShouldBe([201]);
        cache.SetCount.ShouldBe(1);
    }

    [Fact]
    public void The_cache_item_survives_the_serializer_the_redis_cache_actually_uses()
    {
        // The tests above swap in an in-memory cache, which never serializes. In production the item
        // goes through CacheSerializer/System.Text.Json, and a shape that failed to round-trip there
        // would silently return an empty recommendation list on every cache hit.
        var serializer = new CacheSerializer(new JsonSerializerOptions(JsonSerializerOptions.Default));

        var bytes = serializer.Serialize(new RecommendationCacheItem { Ids = [201, 202, 203] });
        var restored = serializer.Deserialize<RecommendationCacheItem>(bytes);

        restored.ShouldNotBeNull();
        restored.Ids.ShouldBe([201L, 202L, 203L]);
    }

    [RequiresMongoDbFact]
    public async Task Without_a_source_channel_recommendations_come_from_the_callers_joined_channels()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        // The caller is in 100; users 1..3 share it and are also in 201.
        await AddMembersAsync(db, channelId: 100, userIds: [99, 1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100, 201);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: null, max: 10, totalCap: 100);

        // 100 is excluded (already joined), 201 is the recommendation.
        result.Ids.ShouldBe([201]);
    }

    [RequiresMongoDbFact]
    public async Task Caller_with_no_joined_channels_gets_nothing_for_the_global_search_variant()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 201);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarChannelIdsAsync(selfUserId: 99, sourceChannelId: null, max: 10, totalCap: 100);

        result.Ids.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Non_positive_max_short_circuits()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddMembersAsync(db, channelId: 100, userIds: [1, 2, 3]);
        await AddMembersAsync(db, channelId: 201, userIds: [1, 2, 3]);
        await AddPublicBroadcastChannelsAsync(db, 100, 201);

        IRecommendationAppService service = CreateService(db);

        (await service.GetSimilarChannelIdsAsync(99, 100, max: 0, totalCap: 100)).Ids.ShouldBeEmpty();
        (await service.GetSimilarBotIdsAsync(99, 500, max: 0, totalCap: 100)).Ids.ShouldBeEmpty();
    }

    // ---- bots ----------------------------------------------------------------------------------------

    [RequiresMongoDbFact]
    public async Task Similar_bots_are_ordered_by_shared_user_count_and_filtered_to_bots()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        // Users 1..3 all talk to bot 500; 601 is shared by all three, 602 by two.
        await AddBotDialogsAsync(db, botUserId: 500, ownerIds: [1, 2, 3]);
        await AddBotDialogsAsync(db, botUserId: 601, ownerIds: [1, 2, 3]);
        await AddBotDialogsAsync(db, botUserId: 602, ownerIds: [1, 2]);
        // 700 shares users too but is a regular account, not a bot → filtered out.
        await AddBotDialogsAsync(db, botUserId: 700, ownerIds: [1, 2, 3]);
        await AddUserAsync(db, 500, bot: true);
        await AddUserAsync(db, 601, bot: true);
        await AddUserAsync(db, 602, bot: true);
        await AddUserAsync(db, 700, bot: false);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarBotIdsAsync(selfUserId: 99, botUserId: 500, max: 10, totalCap: 100);

        result.Ids.ShouldBe([601, 602]);
        result.TotalCount.ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task Source_bot_and_caller_are_excluded_from_bot_recommendations()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddBotDialogsAsync(db, botUserId: 500, ownerIds: [1, 2, 3]);
        // The caller happens to be a bot in this fixture; it must still never be recommended.
        await AddBotDialogsAsync(db, botUserId: 99, ownerIds: [1, 2, 3]);
        await AddUserAsync(db, 500, bot: true);
        await AddUserAsync(db, 99, bot: true);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarBotIdsAsync(selfUserId: 99, botUserId: 500, max: 10, totalCap: 100);

        result.Ids.ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Deleted_dialogs_and_non_user_peers_are_ignored_for_bots()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddBotDialogsAsync(db, botUserId: 500, ownerIds: [1, 2, 3]);
        await AddBotDialogsAsync(db, botUserId: 601, ownerIds: [1, 2, 3]);
        // A channel dialog with the same numeric id must not be mistaken for a bot dialog.
        await AddDialogAsync(db, ownerId: 1, toPeerId: 602, toPeerType: (int)PeerType.Channel, isDeleted: false);
        await AddDialogAsync(db, ownerId: 2, toPeerId: 603, toPeerType: PeerTypeUser, isDeleted: true);
        await AddUserAsync(db, 500, bot: true);
        await AddUserAsync(db, 601, bot: true);
        await AddUserAsync(db, 602, bot: true);
        await AddUserAsync(db, 603, bot: true);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarBotIdsAsync(selfUserId: 99, botUserId: 500, max: 10, totalCap: 100);

        result.Ids.ShouldBe([601]);
    }

    [RequiresMongoDbFact]
    public async Task Bot_with_no_users_yields_no_recommendations()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var db = mongo.Database;

        await AddUserAsync(db, 500, bot: true);

        IRecommendationAppService service = CreateService(db);
        var result = await service.GetSimilarBotIdsAsync(selfUserId: 99, botUserId: 500, max: 10, totalCap: 100);

        result.Ids.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    // ---- fixture builders ----------------------------------------------------------------------------

    /// <summary>
    /// Each test gets a throwaway in-memory cache, so cases stay independent of one another and of
    /// the 10-minute TTL the production Redis cache uses.
    /// </summary>
    private static RecommendationAppService CreateService(IMongoDatabase db, ICacheManager<RecommendationCacheItem>? cache = null)
    {
        return new RecommendationAppService(db, cache ?? new FakeCacheManager<RecommendationCacheItem>());
    }

    private sealed class FakeCacheManager<TCacheItem> : ICacheManager<TCacheItem> where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _items = [];

        public int SetCount { get; private set; }

        public Task<TCacheItem?> GetAsync(string key)
        {
            _items.TryGetValue(key, out var item);
            return Task.FromResult(item);
        }

        public Task<IDictionary<string, TCacheItem>> GetManyAsync(IReadOnlyList<string> keys)
        {
            IDictionary<string, TCacheItem> found = keys
                .Where(_items.ContainsKey)
                .ToDictionary(k => k, k => _items[k]);

            return Task.FromResult(found);
        }

        public Task RemoveAsync(string key)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task SetAsync(string key, TCacheItem value, int ttlInSeconds = -1)
        {
            _items[key] = value;
            SetCount++;
            return Task.CompletedTask;
        }
    }

    private static IMongoCollection<BsonDocument> Members(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");

    private static IMongoCollection<BsonDocument> ChannelsCollection(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>("eventflow-channelreadmodel");

    private static IMongoCollection<BsonDocument> Dialogs(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>("eventflow-dialogreadmodel");

    private static IMongoCollection<BsonDocument> UsersCollection(IMongoDatabase db) =>
        db.GetCollection<BsonDocument>("eventflow-userreadmodel");

    private static async Task AddMembersAsync(IMongoDatabase db, long channelId, long[] userIds)
    {
        foreach (var userId in userIds)
        {
            await AddMemberAsync(db, channelId, userId);
        }
    }

    private static Task AddMemberAsync(IMongoDatabase db, long channelId, long userId,
        bool left = false, bool kicked = false, bool isBot = false)
    {
        return Members(db).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"channelmember-{channelId}-{userId}-{left}-{kicked}",
            ["ChannelId"] = channelId,
            ["UserId"] = userId,
            ["Left"] = left,
            ["Kicked"] = kicked,
            ["IsBot"] = isBot
        });
    }

    private static async Task AddPublicBroadcastChannelsAsync(IMongoDatabase db, params long[] channelIds)
    {
        foreach (var channelId in channelIds)
        {
            await AddChannelAsync(db, channelId, broadcast: true, userName: $"channel{channelId}", isDeleted: false);
        }
    }

    private static Task AddChannelAsync(IMongoDatabase db, long channelId, bool broadcast, string userName, bool isDeleted)
    {
        return ChannelsCollection(db).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"channel-{channelId}",
            ["ChannelId"] = channelId,
            ["Broadcast"] = broadcast,
            ["UserName"] = userName,
            ["IsDeleted"] = isDeleted
        });
    }

    private static async Task AddBotDialogsAsync(IMongoDatabase db, long botUserId, long[] ownerIds)
    {
        foreach (var ownerId in ownerIds)
        {
            await AddDialogAsync(db, ownerId, botUserId, PeerTypeUser, isDeleted: false);
        }
    }

    private static Task AddDialogAsync(IMongoDatabase db, long ownerId, long toPeerId, int toPeerType, bool isDeleted)
    {
        return Dialogs(db).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"dialog-{ownerId}-{toPeerType}-{toPeerId}",
            ["OwnerId"] = ownerId,
            ["ToPeerId"] = toPeerId,
            ["ToPeerType"] = toPeerType,
            ["IsDeleted"] = isDeleted
        });
    }

    private static Task AddUserAsync(IMongoDatabase db, long userId, bool bot)
    {
        return UsersCollection(db).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"user-{userId}",
            ["UserId"] = userId,
            ["Bot"] = bot,
            ["IsDeleted"] = false
        });
    }
}
