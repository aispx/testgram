using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Stickers;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class SetStickerSetThumbHandler(
    IMongoDatabase mongoDatabase,
    ILogger<SetStickerSetThumbHandler> logger) : RpcResultObjectHandler<Schema.Stickers.RequestSetStickerSetThumb, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, Schema.Stickers.RequestSetStickerSetThumb obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");

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
            logger.LogWarning("User {UserId} tried to set thumb for set {SetId} created by {CreatorUserId}",
                input.UserId, setId, creatorUserId);
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var accessHash = GetInt64(setDoc["AccessHash"]);
        var title = setDoc["Title"].AsString;
        var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
        var count = GetInt32(setDoc["Count"]);
        
        if (obj.Thumb != null)
        {
            if (obj.Thumb is TInputDocument inputDoc)
            {
                setDoc["ThumbId"] = inputDoc.Id;
                logger.LogInformation("Set thumb {ThumbId} for sticker set {SetId}", inputDoc.Id, setId);
            }
        }
        else if (obj.ThumbDocumentId != 0)
        {
            setDoc["ThumbId"] = obj.ThumbDocumentId;
            logger.LogInformation("Set thumb {ThumbId} for sticker set {SetId}", obj.ThumbDocumentId, setId);
        }

        await setCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            setDoc);

        return new TStickerSet
        {
            Set = new Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = title,
                ShortName = shortName,
                Count = count,
                Hash = 0
            },
            Packs = [],
            Documents = [],
            Keywords = []
        };
    }

    private static long GetInt64(BsonValue v) => v.BsonType switch
    {
        BsonType.Int64 => v.AsInt64,
        BsonType.Int32 => v.AsInt32,
        BsonType.Double => (long)v.AsDouble,
        _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
    };

    private static int GetInt32(BsonValue v) => v.BsonType switch
    {
        BsonType.Int32 => v.AsInt32,
        BsonType.Int64 => (int)v.AsInt64,
        BsonType.Double => (int)v.AsDouble,
        _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int32")
    };
}
