using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal static class MonoForumTopicStateHelper
{
    private const string CollectionName = "monoforum_topic_state";

    public static string BuildId(long ownerPeerId, long userId, Peer savedPeer)
    {
        return $"mfstate-{ownerPeerId}-{userId}-{savedPeer.PeerType}-{savedPeer.PeerId}";
    }

    public static IMongoCollection<BsonDocument> GetCollection(IMongoDatabase mongoDatabase)
    {
        return mongoDatabase.GetCollection<BsonDocument>(CollectionName);
    }

    public static async Task<Dictionary<string, BsonDocument>> GetStatesAsync(
        IMongoDatabase mongoDatabase,
        long ownerPeerId,
        long userId,
        IEnumerable<Peer> savedPeers)
    {
        var peers = savedPeers.DistinctBy(p => $"{p.PeerType}:{p.PeerId}").ToList();
        if (peers.Count == 0)
        {
            return new Dictionary<string, BsonDocument>();
        }

        var ids = peers.Select(p => BuildId(ownerPeerId, userId, p)).ToList();
        var docs = await GetCollection(mongoDatabase)
            .Find(Builders<BsonDocument>.Filter.In("_id", ids))
            .ToListAsync();

        return docs.ToDictionary(x => x["_id"].AsString, x => x);
    }

    public static async Task UpsertReadInboxMaxIdAsync(
        IMongoDatabase mongoDatabase,
        long ownerPeerId,
        long userId,
        Peer savedPeer,
        int maxId)
    {
        var collection = GetCollection(mongoDatabase);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", BuildId(ownerPeerId, userId, savedPeer));
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("OwnerPeerId", ownerPeerId)
            .SetOnInsert("UserId", userId)
            .SetOnInsert("SavedPeerType", savedPeer.PeerType.ToString())
            .SetOnInsert("SavedPeerId", savedPeer.PeerId)
            .Max("ReadInboxMaxId", maxId);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public static async Task UpsertUnreadMarkAsync(
        IMongoDatabase mongoDatabase,
        long ownerPeerId,
        long userId,
        Peer savedPeer,
        bool unread)
    {
        var collection = GetCollection(mongoDatabase);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", BuildId(ownerPeerId, userId, savedPeer));
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("OwnerPeerId", ownerPeerId)
            .SetOnInsert("UserId", userId)
            .SetOnInsert("SavedPeerType", savedPeer.PeerType.ToString())
            .SetOnInsert("SavedPeerId", savedPeer.PeerId)
            .Set("UnreadMark", unread);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public static async Task<List<Peer>> GetUnreadMarkedPeersAsync(IMongoDatabase mongoDatabase, long ownerPeerId, long userId)
    {
        var docs = await GetCollection(mongoDatabase)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId),
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("UnreadMark", true)))
            .ToListAsync();

        return docs.Select(d => new Peer(
            Enum.Parse<PeerType>(d["SavedPeerType"].AsString, true),
            d["SavedPeerId"].ToInt64())).ToList();
    }

    public static int GetReadInboxMaxId(BsonDocument? state)
    {
        return state != null && state.TryGetValue("ReadInboxMaxId", out var value) && value.IsInt32 ? value.AsInt32 : 0;
    }

    public static int GetReadOutboxMaxId(BsonDocument? state)
    {
        return state != null && state.TryGetValue("ReadOutboxMaxId", out var value) && value.IsInt32 ? value.AsInt32 : 0;
    }

    public static bool GetUnreadMark(BsonDocument? state)
    {
        return state != null && state.TryGetValue("UnreadMark", out var value) && value.IsBoolean && value.AsBoolean;
    }
}
