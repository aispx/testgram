using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Computes recommendations by subscriber-base overlap, see https://corefork.telegram.org/api/recommend
/// <para>
/// Channels: two channels are "similar" when many of the same users are subscribed to both. The
/// source audience is sampled (bounded by <see cref="SampleSize"/>) so a huge channel cannot turn a
/// single RPC into a full collection scan.
/// </para>
/// <para>
/// Bots: the closest available analogue of a bot's "subscriber base" is the set of users who have a
/// private dialog with it, so overlap is computed over <c>eventflow-dialogreadmodel</c>.
/// </para>
/// <para>
/// Clients call these methods on every channel join, on every profile tab open and from global
/// search, so the aggregation result is cached per source. Everything caller-specific (excluding
/// channels the caller already joined, excluding the caller itself) is applied *after* the cache, so
/// one entry serves every caller and both premium tiers.
/// </para>
/// </summary>
public class RecommendationAppService(
    IMongoDatabase database,
    ICacheManager<RecommendationCacheItem> cacheManager)
    : IRecommendationAppService, ITransientDependency
{
    /// <summary>Max users sampled from the source audience, across all source channels.</summary>
    private const int SampleSize = 1000;

    /// <summary>
    /// Floor for the per-source-channel audience sample. Without a floor, the global variant would
    /// divide <see cref="SampleSize"/> across up to <see cref="SourceChannelLimit"/> channels and
    /// sample too few subscribers from each to produce meaningful overlap.
    /// </summary>
    private const int MinSamplePerChannel = 50;

    /// <summary>Max overlap candidates carried past the aggregation.</summary>
    private const int CandidateLimit = 300;

    /// <summary>Max source channels used when recommending from all joined channels.</summary>
    private const int SourceChannelLimit = 50;

    /// <summary>How long a computed candidate list stays valid.</summary>
    private const int CacheTtlInSeconds = 600;

    private const int PeerTypeUser = (int)PeerType.User;

    private IMongoCollection<BsonDocument> ChannelMembers =>
        database.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");

    private IMongoCollection<BsonDocument> Channels =>
        database.GetCollection<BsonDocument>("eventflow-channelreadmodel");

    private IMongoCollection<BsonDocument> Dialogs =>
        database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");

    private IMongoCollection<BsonDocument> Users =>
        database.GetCollection<BsonDocument>("eventflow-userreadmodel");

    public async Task<RecommendationResult> GetSimilarChannelIdsAsync(long selfUserId, long? sourceChannelId, int max, int totalCap)
    {
        if (max <= 0)
        {
            return RecommendationResult.Empty;
        }

        // Channels the caller is already in are never recommended: they are not actionable in the
        // "similar channels" UI, and for the global-search variant they are the query itself.
        var joinedChannelIds = await GetJoinedChannelIdsAsync(selfUserId);

        List<long> sourceChannelIds;
        string cacheKey;
        if (sourceChannelId.HasValue)
        {
            sourceChannelIds = [sourceChannelId.Value];
            cacheKey = $"recommend:channel:{sourceChannelId.Value}";
        }
        else
        {
            // No channel passed: recommend based on everything the caller has joined. Sorted so the
            // same membership set always produces the same sample and the same cache entry.
            sourceChannelIds = [.. joinedChannelIds.Order().Take(SourceChannelLimit)];
            if (sourceChannelIds.Count == 0)
            {
                return RecommendationResult.Empty;
            }

            cacheKey = $"recommend:channel:self:{selfUserId}";
        }

        var candidates = await GetCachedAsync(cacheKey, () => ComputeSimilarChannelIdsAsync(sourceChannelIds));

        // Caller-specific filtering, deliberately outside the cache.
        var excluded = new HashSet<long>(joinedChannelIds);
        foreach (var id in sourceChannelIds)
        {
            excluded.Add(id);
        }

        var ordered = candidates.Where(p => !excluded.Contains(p)).ToList();

        return BuildResult(ordered, max, totalCap);
    }

    public async Task<RecommendationResult> GetSimilarBotIdsAsync(long selfUserId, long botUserId, int max, int totalCap)
    {
        if (max <= 0)
        {
            return RecommendationResult.Empty;
        }

        var candidates = await GetCachedAsync($"recommend:bot:{botUserId}", () => ComputeSimilarBotIdsAsync(botUserId));

        // The caller is never recommended to itself, even when it also has a bot account.
        var ordered = candidates.Where(p => p != selfUserId).ToList();

        return BuildResult(ordered, max, totalCap);
    }

    /// <summary>
    /// Truncates the candidate list to the caller's limit, and the reported total to what a premium
    /// account would actually receive — clients turn <c>count - ids.Count</c> into "unlock N more",
    /// so a total taken from the raw candidate pool would advertise channels nobody can ever get.
    /// </summary>
    private static RecommendationResult BuildResult(List<long> ordered, int max, int totalCap)
    {
        if (ordered.Count == 0)
        {
            return RecommendationResult.Empty;
        }

        var cap = Math.Max(totalCap, max);

        return new RecommendationResult([.. ordered.Take(max)], Math.Min(ordered.Count, cap));
    }

    private async Task<List<long>> GetCachedAsync(string cacheKey, Func<Task<List<long>>> factory)
    {
        var cached = await cacheManager.GetAsync(cacheKey);
        if (cached != null)
        {
            return cached.Ids;
        }

        var ids = await factory();
        await cacheManager.SetAsync(cacheKey, new RecommendationCacheItem { Ids = ids }, CacheTtlInSeconds);

        return ids;
    }

    private async Task<List<long>> ComputeSimilarChannelIdsAsync(List<long> sourceChannelIds)
    {
        var audience = await GetChannelAudienceAsync(sourceChannelIds);
        if (audience.Count == 0)
        {
            return [];
        }

        var candidates = await GetOverlappingChannelIdsAsync(audience, [.. sourceChannelIds]);
        if (candidates.Count == 0)
        {
            return [];
        }

        // Only public broadcast channels can be recommended.
        var publicChannelIds = await GetPublicBroadcastChannelIdsAsync(candidates);

        return [.. candidates.Where(publicChannelIds.Contains)];
    }

    private async Task<List<long>> ComputeSimilarBotIdsAsync(long botUserId)
    {
        var audience = await GetBotAudienceAsync(botUserId);
        if (audience.Count == 0)
        {
            return [];
        }

        var candidates = await GetOverlappingBotIdsAsync(audience, [botUserId]);
        if (candidates.Count == 0)
        {
            return [];
        }

        var botIds = await GetBotUserIdsAsync(candidates);

        return [.. candidates.Where(botIds.Contains)];
    }

    private async Task<List<long>> GetJoinedChannelIdsAsync(long selfUserId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", selfUserId)
                     & Builders<BsonDocument>.Filter.Ne("Left", true)
                     & Builders<BsonDocument>.Filter.Ne("Kicked", true);

        var docs = await ChannelMembers
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("ChannelId"))
            .ToListAsync();

        return [.. docs.Select(p => GetInt64(p["ChannelId"])).Distinct()];
    }

    /// <summary>
    /// Samples the subscribers of every source channel separately: a single query with a global limit
    /// would let the first (largest) channel consume the whole sample, collapsing the global-search
    /// variant into "similar to one channel". Sorted by UserId so the sample is reproducible instead
    /// of depending on collection natural order.
    /// </summary>
    private async Task<List<long>> GetChannelAudienceAsync(List<long> channelIds)
    {
        var perChannelLimit = Math.Max(SampleSize / channelIds.Count, MinSamplePerChannel);
        var audience = new HashSet<long>();

        foreach (var channelId in channelIds)
        {
            var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)
                         & Builders<BsonDocument>.Filter.Ne("Left", true)
                         & Builders<BsonDocument>.Filter.Ne("Kicked", true)
                         & Builders<BsonDocument>.Filter.Ne("IsBot", true);

            var docs = await ChannelMembers
                .Find(filter)
                .Sort(Builders<BsonDocument>.Sort.Ascending("UserId"))
                .Project(Builders<BsonDocument>.Projection.Include("UserId"))
                .Limit(perChannelLimit)
                .ToListAsync();

            foreach (var doc in docs)
            {
                audience.Add(GetInt64(doc["UserId"]));
            }
        }

        return [.. audience];
    }

    private async Task<List<long>> GetOverlappingChannelIdsAsync(List<long> audience, HashSet<long> excludedChannelIds)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["UserId"] = new BsonDocument("$in", new BsonArray(audience.Select(p => new BsonInt64(p)))),
                ["Left"] = new BsonDocument("$ne", true),
                ["Kicked"] = new BsonDocument("$ne", true),
                ["ChannelId"] = new BsonDocument("$nin", new BsonArray(excludedChannelIds.Select(p => new BsonInt64(p))))
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = "$ChannelId",
                ["shared"] = new BsonDocument("$sum", 1)
            }),
            new BsonDocument("$sort", new BsonDocument { ["shared"] = -1, ["_id"] = 1 }),
            new BsonDocument("$limit", CandidateLimit)
        };

        var docs = await ChannelMembers.Aggregate<BsonDocument>(pipeline).ToListAsync();

        return [.. docs.Select(p => GetInt64(p["_id"]))];
    }

    private async Task<HashSet<long>> GetPublicBroadcastChannelIdsAsync(List<long> channelIds)
    {
        var filter = Builders<BsonDocument>.Filter.In("ChannelId", channelIds.Select(p => (BsonValue)new BsonInt64(p)))
                     & Builders<BsonDocument>.Filter.Eq("Broadcast", true)
                     & Builders<BsonDocument>.Filter.Ne("IsDeleted", true)
                     & Builders<BsonDocument>.Filter.Ne("UserName", BsonNull.Value)
                     & Builders<BsonDocument>.Filter.Ne("UserName", string.Empty);

        var docs = await Channels
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("ChannelId"))
            .ToListAsync();

        return [.. docs.Select(p => GetInt64(p["ChannelId"]))];
    }

    private async Task<List<long>> GetBotAudienceAsync(long botUserId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("ToPeerId", botUserId)
                     & Builders<BsonDocument>.Filter.Eq("ToPeerType", PeerTypeUser)
                     & Builders<BsonDocument>.Filter.Ne("IsDeleted", true);

        var docs = await Dialogs
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("OwnerId"))
            .Project(Builders<BsonDocument>.Projection.Include("OwnerId"))
            .Limit(SampleSize)
            .ToListAsync();

        return [.. docs.Select(p => GetInt64(p["OwnerId"])).Distinct()];
    }

    private async Task<List<long>> GetOverlappingBotIdsAsync(List<long> audience, long[] excludedUserIds)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument
            {
                ["OwnerId"] = new BsonDocument("$in", new BsonArray(audience.Select(p => new BsonInt64(p)))),
                ["ToPeerType"] = PeerTypeUser,
                ["IsDeleted"] = new BsonDocument("$ne", true),
                ["ToPeerId"] = new BsonDocument("$nin", new BsonArray(excludedUserIds.Select(p => new BsonInt64(p))))
            }),
            new BsonDocument("$group", new BsonDocument
            {
                ["_id"] = "$ToPeerId",
                ["shared"] = new BsonDocument("$sum", 1)
            }),
            new BsonDocument("$sort", new BsonDocument { ["shared"] = -1, ["_id"] = 1 }),
            new BsonDocument("$limit", CandidateLimit)
        };

        var docs = await Dialogs.Aggregate<BsonDocument>(pipeline).ToListAsync();

        return [.. docs.Select(p => GetInt64(p["_id"]))];
    }

    private async Task<HashSet<long>> GetBotUserIdsAsync(List<long> userIds)
    {
        var filter = Builders<BsonDocument>.Filter.In("UserId", userIds.Select(p => (BsonValue)new BsonInt64(p)))
                     & Builders<BsonDocument>.Filter.Eq("Bot", true)
                     & Builders<BsonDocument>.Filter.Ne("IsDeleted", true);

        var docs = await Users
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("UserId"))
            .ToListAsync();

        return [.. docs.Select(p => GetInt64(p["UserId"]))];
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {value.BsonType} to Int64")
        };
    }
}
