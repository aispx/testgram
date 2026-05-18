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

    private static string DocId(long userId, long targetPeerId) => $"{userId}-{targetPeerId}";

    public async Task BlockAsync(long userId, long targetPeerId)
    {
        if (userId == targetPeerId) return;
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        var doc = new BsonDocument
        {
            ["_id"] = DocId(userId, targetPeerId),
            ["UserId"] = userId,
            ["TargetPeerId"] = targetPeerId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await col.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, targetPeerId)),
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task<bool> IsBlockedAsync(long userId, long targetPeerId)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        return await col.Find(Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, targetPeerId)))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .Limit(1)
            .AnyAsync();
    }

    public async Task UnblockAsync(long userId, long targetPeerId)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(CollectionName);
        await col.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId(userId, targetPeerId)));
    }
}