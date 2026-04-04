using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Add/remove sticker from recent stickers list
/// Possible errors
/// Code Type Description
/// 400 STICKER_ID_INVALID The provided sticker ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.saveRecentSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveRecentStickerHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSaveRecentSticker, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSaveRecentSticker obj)
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

        var recentCol = mongoDatabase.GetCollection<BsonDocument>("recent_stickers");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("DocumentId", inputDoc.Id),
            Builders<BsonDocument>.Filter.Eq("Attached", obj.Attached)
        );

        if (obj.Unsave)
        {
            // Remove from recent
            await recentCol.DeleteOneAsync(filter);
        }
        else
        {
            // Add or update recent
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var update = Builders<BsonDocument>.Update
                .Set("UserId", input.UserId)
                .Set("DocumentId", inputDoc.Id)
                .Set("Attached", obj.Attached)
                .Set("Date", now);

            await recentCol.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

            // Keep only last 20 recent stickers per user
            var allRecent = await recentCol
                .Find(Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("Attached", obj.Attached)
                ))
                .SortByDescending(x => x["Date"])
                .ToListAsync();

            if (allRecent.Count > 20)
            {
                var toDelete = allRecent.Skip(20).Select(d => d["_id"]).ToList();
                await recentCol.DeleteManyAsync(Builders<BsonDocument>.Filter.In("_id", toDelete));
            }
        }

        return new TBoolTrue();
    }
}