using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <inheritdoc cref="IPinnedMessageResolver" />
public class PinnedMessageResolver(IMongoDatabase mongoDatabase, ILogger<PinnedMessageResolver> logger)
    : IPinnedMessageResolver, ITransientDependency
{
    public Task<int?> GetChannelPinnedMsgIdAsync(long channelId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerPeerId", channelId),
            Builders<BsonDocument>.Filter.Eq("Pinned", true)
        );

        return GetLatestPinnedMsgIdAsync(filter, $"channel {channelId}");
    }

    public Task<int?> GetPrivateChatPinnedMsgIdAsync(long selfUserId, long targetUserId)
    {
        // ToPeerType is stored as the numeric enum value, and Saved Messages keeps peerType=Self, so
        // both variants have to be accepted — ToPeerId is what identifies the chat.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerPeerId", selfUserId),
            Builders<BsonDocument>.Filter.In("ToPeerType", new[] { (int)PeerType.User, (int)PeerType.Self }),
            Builders<BsonDocument>.Filter.Eq("ToPeerId", targetUserId),
            Builders<BsonDocument>.Filter.Eq("Pinned", true)
        );

        return GetLatestPinnedMsgIdAsync(filter, $"user {selfUserId} -> {targetUserId}");
    }

    private async Task<int?> GetLatestPinnedMsgIdAsync(FilterDefinition<BsonDocument> filter, string peerDescription)
    {
        try
        {
            var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
            var pinnedMessage = await collection.Find(filter)
                .SortByDescending(m => m["MessageId"])
                .Limit(1)
                .FirstOrDefaultAsync();

            return pinnedMessage?["MessageId"].AsInt32;
        }
        catch (Exception ex)
        {
            // pinned_msg_id is decoration on top of the full peer: a failure here must not break the
            // whole *Full response.
            logger.LogWarning(ex, "Failed to load pinned message for {Peer}", peerDescription);
            return null;
        }
    }
}
