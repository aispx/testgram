using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Caching;

// Item 22: persist user-to-user blocks in MongoDB so contacts.block / contacts.unblock
// actually take effect and downstream send/typing handlers can enforce them. The
// previous no-op stub silently swallowed every block, leaving USER_IS_BLOCKED /
// YOU_BLOCKED_USER unreachable and letting blocked peers keep delivering messages.
public class BlockCacheAppService(IMongoDatabase mongoDatabase) : IBlockCacheAppService, ISingletonDependency
{
    private const string CollectionName = "user-blocks";

    private static string DocId(long userId, long targetPeerId, PeerType targetPeerType, bool myStoriesFrom)
    {
        // Keep the original user-to-user id shape so existing send/typing guards and
        // already persisted user blocks continue to work. Non-user peers are typed to
        // avoid collisions between blocklist entries with the same numeric id.
        if (targetPeerType == PeerType.User)
        {
            return myStoriesFrom ? $"{userId}-stories-{targetPeerId}" : $"{userId}-{targetPeerId}";
        }

        return myStoriesFrom
            ? $"{userId}-stories-{(int)targetPeerType}-{targetPeerId}"
            : $"{userId}-{(int)targetPeerType}-{targetPeerId}";
    }

    private static string LegacyDocId(long userId, long targetPeerId, bool myStoriesFrom)
        => myStoriesFrom ? $"{userId}-stories-{targetPeerId}" : $"{userId}-{targetPeerId}";

    private static BsonDocument ToDocument(
        long userId,
        Peer peer,
        bool myStoriesFrom,
        int date)
    {
        return new BsonDocument
        {
            ["_id"] = DocId(userId, peer.PeerId, peer.PeerType, myStoriesFrom),
            ["UserId"] = userId,
            ["TargetPeerId"] = peer.PeerId,
            ["TargetPeerType"] = (int)peer.PeerType,
            ["MyStoriesFrom"] = myStoriesFrom,
            ["Date"] = date,
        };
    }

    private static FilterDefinition<BsonDocument> UserListFilter(long userId, bool myStoriesFrom)
    {
        var builder = Builders<BsonDocument>.Filter;
        var listFilter = myStoriesFrom
            ? builder.Eq("MyStoriesFrom", true)
            : builder.Or(builder.Eq("MyStoriesFrom", false), builder.Exists("MyStoriesFrom", false));

        return builder.And(builder.Eq("UserId", userId), listFilter);
    }

    public async Task BlockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User, bool myStoriesFrom = false)
    {
        if (userId == targetPeerId) return;
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var doc = new BsonDocument
        {
            ["_id"] = DocId(userId, targetPeerId, targetPeerType, myStoriesFrom),
            ["UserId"] = userId,
            ["TargetPeerId"] = targetPeerId,
            ["TargetPeerType"] = (int)targetPeerType,
            ["MyStoriesFrom"] = myStoriesFrom,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await col.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, targetPeerId, targetPeerType, myStoriesFrom)),
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task<BlockedPeerCachePage> GetBlockedAsync(long userId, int offset, int limit, bool myStoriesFrom = false)
    {
        offset = Math.Max(0, offset);
        limit = limit <= 0 ? 100 : limit;

        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var filter = UserListFilter(userId, myStoriesFrom);
        var count = (int)await col.CountDocumentsAsync(filter);
        var docs = await col.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("Date"))
            .Skip(offset)
            .Limit(limit)
            .ToListAsync();

        var items = docs.Select(doc =>
        {
            var peerType = doc.TryGetValue("TargetPeerType", out var targetPeerType) && targetPeerType.IsInt32
                ? (PeerType)targetPeerType.AsInt32
                : PeerType.User;
            return new BlockedPeerCacheItem(
                peerType,
                doc.GetValue("TargetPeerId", 0L).ToInt64(),
                doc.GetValue("Date", 0).ToInt32());
        }).ToList();

        return new BlockedPeerCachePage(count, items);
    }

    public async Task<bool> IsBlockedAsync(long userId, long targetPeerId)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        return await col.Find(Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, targetPeerId, PeerType.User, false)))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .Limit(1)
            .AnyAsync();
    }

    public async Task UnblockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User, bool myStoriesFrom = false)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var builder = Builders<BsonDocument>.Filter;
        var id = DocId(userId, targetPeerId, targetPeerType, myStoriesFrom);
        var legacyId = LegacyDocId(userId, targetPeerId, myStoriesFrom);
        var filter = id == legacyId
            ? builder.Eq("_id", id)
            : builder.Or(builder.Eq("_id", id), builder.Eq("_id", legacyId));
        await col.DeleteManyAsync(filter);
    }

    public async Task ReplaceBlockedAsync(long userId, IReadOnlyCollection<Peer> peers, bool myStoriesFrom = false)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var existingDocs = await col.Find(UserListFilter(userId, myStoriesFrom)).ToListAsync();
        var existingDates = existingDocs
            .Where(p => p.TryGetValue("_id", out var id) && id.IsString)
            .ToDictionary(
                p => p["_id"].AsString,
                p => p.GetValue("Date", 0).ToInt32());
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var docs = peers
            .Where(p => p.PeerId != userId && p.PeerType is PeerType.User or PeerType.Chat or PeerType.Channel)
            .DistinctBy(p => (p.PeerType, p.PeerId))
            .Select(p =>
            {
                var id = DocId(userId, p.PeerId, p.PeerType, myStoriesFrom);
                return ToDocument(userId, p, myStoriesFrom, existingDates.GetValueOrDefault(id, now));
            })
            .ToList();

        if (docs.Count > 0)
        {
            var replaceModels = docs
                .Select(doc => new ReplaceOneModel<BsonDocument>(
                    Builders<BsonDocument>.Filter.Eq("_id", doc["_id"].AsString),
                    doc)
                {
                    IsUpsert = true,
                })
                .Cast<WriteModel<BsonDocument>>()
                .ToList();
            await col.BulkWriteAsync(replaceModels, new BulkWriteOptions { IsOrdered = true });
        }

        var desiredIds = docs.Select(p => p["_id"].AsString).ToHashSet(StringComparer.Ordinal);
        var staleIds = existingDocs
            .Select(p => p.TryGetValue("_id", out var id) && id.IsString ? id.AsString : null)
            .Where(id => id != null && !desiredIds.Contains(id))
            .Select(id => id!)
            .ToList();
        if (staleIds.Count > 0)
        {
            await col.DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", staleIds));
        }
    }
}
