using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;
/// <summary>
/// Deletes a stickerset we created.
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.deleteStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteStickerSetHandler(
    IMongoDatabase mongoDatabase,
    ILogger<DeleteStickerSetHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestDeleteStickerSet, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stickers.RequestDeleteStickerSet obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var userSetCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userinstalledstickersetreadmodel");

        BsonDocument? setDoc = null;

        if (obj.Stickerset is TInputStickerSetID setById)
        {
            setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", setById.Id)).FirstOrDefaultAsync();
        }
        else if (obj.Stickerset is TInputStickerSetShortName shortNameSet)
        {
            setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("ShortName", shortNameSet.ShortName)).FirstOrDefaultAsync();
            if (setDoc == null)
            {
                setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("Slug", shortNameSet.ShortName)).FirstOrDefaultAsync();
            }
        }

        if (setDoc == null)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setId = GetInt64(setDoc["StickerSetId"]);

        // Check if user is the creator of the sticker set
        var creatorUserId = setDoc.Contains("CreatorUserId") ? GetInt64(setDoc["CreatorUserId"]) : 0;
        if (creatorUserId != input.UserId)
        {
            logger.LogWarning("User {UserId} tried to delete set {SetId} created by {CreatorUserId}",
                input.UserId, setId, creatorUserId);
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        // Delete from main collection
        await setCol.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("StickerSetId", setId));

        // Delete from user installed sets
        await userSetCol.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("StickerSetId", setId));

        logger.LogInformation("Deleted sticker set {SetId} by user {UserId}", setId, input.UserId);

        return new TBoolTrue();
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }
}