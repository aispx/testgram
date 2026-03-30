using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using IStickerSet = MyTelegram.Schema.IStickerSet;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TDocumentEmpty = MyTelegram.Schema.TDocumentEmpty;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetAllStickersHandler(
    IMongoDatabase mongoDatabase,
    ILogger<GetAllStickersHandler> logger) : RpcResultObjectHandler<RequestGetAllStickers, IAllStickers>
{
    protected override async Task<IAllStickers> HandleCoreAsync(IRequestInput input, RequestGetAllStickers obj)
    {
        var userSetCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userinstalledstickersetreadmodel");
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        var userSets = await userSetCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)).ToListAsync();
        
        var sets = new List<Schema.TStickerSet>();
        
        foreach (var userSet in userSets)
        {
            var setId = GetInt64(userSet["StickerSetId"]);
            var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", setId)).FirstOrDefaultAsync();
            
            if (setDoc == null)
            {
                continue;
            }
            
            var accessHash = GetInt64(setDoc["AccessHash"]);
            var title = setDoc["Title"].AsString;
            var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
            var count = GetInt32(setDoc["Count"]);
            
            sets.Add(new Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = title,
                ShortName = shortName,
                Count = count,
                Hash = 0
            });
        }
        
        var resultHash = ComputeHash(sets.Count);
        
        logger.LogInformation("GetAllStickers for user {UserId}: {Count} sets", input.UserId, sets.Count);
        
        return new TAllStickers
        {
            Hash = resultHash,
            Sets = new TVector<IStickerSet>(sets.Select(s => (IStickerSet)s).ToList())
        };
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

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int32")
        };
    }

    private static long ComputeHash(int count)
    {
        return count.GetHashCode() ^ 0x1234567890L;
    }
}
