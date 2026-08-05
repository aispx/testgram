using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Resolves the 32-bit <c>msg_id</c> that addresses a user-owned saved star gift.
/// </summary>
/// <remarks>
/// <para>
/// Layer 229 addresses user-owned gifts through
/// <c>inputSavedStarGiftUser#69279795 msg_id:int</c> — a 32-bit field. Gifts that were never
/// anchored to a service message have <c>MessageId == 0</c>, and the handler used to fall back to
/// casting the 64-bit <c>RandomId</c> into that field. The cast silently truncates:
/// <c>6987717616945285832</c> arrives at the client as <c>1824737992</c>, which matches no gift on
/// the way back, so convert/upgrade/transfer all fail.
/// </para>
/// <para>
/// Instead every unanchored gift gets a stable 32-bit local id from the <c>counters</c> collection,
/// persisted on the document so the value survives restarts and round-trips intact.
/// </para>
/// </remarks>
public static class SavedStarGiftMsgIdHelper
{
    private const string CounterId = "saved-star-gift-msg-id";

    /// <summary>
    /// Local ids start well above realistic service-message ids so a local id can never collide
    /// with a real <see cref="SavedStarGiftDocument.MessageId"/>.
    /// </summary>
    private const int LocalMsgIdBase = 1_000_000_000;

    /// <summary>
    /// Returns the <c>msg_id</c> to advertise for <paramref name="doc"/>, allocating and persisting
    /// a local id when the gift is not anchored to a service message.
    /// </summary>
    public static async Task<int> ResolveAsync(IMongoDatabase database, SavedStarGiftDocument doc)
    {
        if (doc.MessageId > 0) return doc.MessageId;
        if (doc.LocalMsgId > 0) return doc.LocalMsgId;

        var localMsgId = await NextLocalMsgIdAsync(database);

        await database.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
            .UpdateOneAsync(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Id, doc.Id),
                Builders<SavedStarGiftDocument>.Update.Set(d => d.LocalMsgId, localMsgId));

        doc.LocalMsgId = localMsgId;
        return localMsgId;
    }

    /// <summary>
    /// Filter matching the gift a client addressed by <paramref name="msgId"/>.
    /// </summary>
    /// <remarks>
    /// <c>RandomId</c> is still matched because upgraded (unique) gifts store a small sequential
    /// unique id there, which fits in 32 bits and is what older clients round-trip.
    /// </remarks>
    public static FilterDefinition<SavedStarGiftDocument> MatchMsgId(int msgId)
    {
        var f = Builders<SavedStarGiftDocument>.Filter;
        return f.Or(
            f.And(f.Gt(d => d.MessageId, 0), f.Eq(d => d.MessageId, msgId)),
            f.And(f.Gt(d => d.LocalMsgId, 0), f.Eq(d => d.LocalMsgId, msgId)),
            f.Eq(d => d.RandomId, msgId));
    }

    private static async Task<int> NextLocalMsgIdAsync(IMongoDatabase database)
    {
        var counter = await database.GetCollection<BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                Builders<BsonDocument>.Filter.Eq("_id", CounterId),
                Builders<BsonDocument>.Update.Inc("seq", 1),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                });

        return LocalMsgIdBase + counter["seq"].AsInt32;
    }
}
