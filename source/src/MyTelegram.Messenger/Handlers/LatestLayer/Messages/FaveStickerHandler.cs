using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark or unmark a sticker as favorite
/// Possible errors
/// Code Type Description
/// 400 STICKER_ID_INVALID The provided sticker ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.faveSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class FaveStickerHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestFaveSticker, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestFaveSticker obj)
    {
        if (obj.Id is not TInputDocument inputDoc)
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
            return null!;
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docExists = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", inputDoc.Id)).AnyAsync();

        if (!docExists)
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
        }

        var favedCol = mongoDatabase.GetCollection<BsonDocument>("faved_stickers");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("DocumentId", inputDoc.Id)
        );

        if (obj.Unfave)
        {
            // Remove from favorites
            await favedCol.DeleteOneAsync(filter);
        }
        else
        {
            // Add to favorites
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var update = Builders<BsonDocument>.Update
                .Set("UserId", input.UserId)
                .Set("DocumentId", inputDoc.Id)
                .Set("Date", now);

            await favedCol.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
        }

        return new TBoolTrue();
    }
}