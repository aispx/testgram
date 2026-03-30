using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Stickers;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class RenameStickerSetHandler(
    IMongoDatabase mongoDatabase,
    ILogger<RenameStickerSetHandler> logger) : RpcResultObjectHandler<Schema.Stickers.RequestRenameStickerSet, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, Schema.Stickers.RequestRenameStickerSet obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var userSetCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userinstalledstickersetreadmodel");

        if (string.IsNullOrWhiteSpace(obj.Title))
        {
            RpcErrors.RpcErrors400.PackTitleInvalid.ThrowRpcError();
        }

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
        var accessHash = GetInt64(setDoc["AccessHash"]);
        var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;

        setDoc["Title"] = obj.Title;
        await setCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            setDoc);

        await userSetCol.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            Builders<BsonDocument>.Update.Set("Title", obj.Title));

        logger.LogInformation("Renamed sticker set {SetId} to '{Title}'", setId, obj.Title);

        return new TStickerSet
        {
            Set = new Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = obj.Title,
                ShortName = shortName,
                Count = GetInt32(setDoc["Count"]),
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
