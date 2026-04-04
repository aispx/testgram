using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Clear recent stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.clearRecentStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ClearRecentStickersHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestClearRecentStickers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestClearRecentStickers obj)
    {
        var recentCol = mongoDatabase.GetCollection<BsonDocument>("recent_stickers");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("Attached", obj.Attached)
        );

        await recentCol.DeleteManyAsync(filter);

        return new TBoolTrue();
    }
}