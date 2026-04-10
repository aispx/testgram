using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TDocumentEmpty = MyTelegram.Schema.TDocumentEmpty;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetMyStickersHandler(
    IMongoDatabase mongoDatabase,
    ILogger<GetMyStickersHandler> logger) : RpcResultObjectHandler<RequestGetMyStickers, IMyStickers>
{
    protected override async Task<IMyStickers> HandleCoreAsync(IRequestInput input, RequestGetMyStickers obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        // Filter by CreatorUserId to get sticker packs created by this user
        var filter = Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId);

        if (obj.OffsetId > 0)
        {
            filter = Builders<BsonDocument>.Filter.And(
                filter,
                Builders<BsonDocument>.Filter.Lt("StickerSetId", obj.OffsetId)
            );
        }

        var mySets = await setCol
            .Find(filter)
            .SortByDescending(x => x["StickerSetId"])
            .Limit(obj.Limit > 0 ? obj.Limit : 100)
            .ToListAsync();

        var stickerSetCovers = new List<IStickerSetCovered>();

        foreach (var setDoc in mySets)
        {
            var setId = GetInt64(setDoc["StickerSetId"]);
            var accessHash = GetInt64(setDoc["AccessHash"]);
            var title = setDoc["Title"].AsString;
            var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
            var count = GetInt32(setDoc["Count"]);

            var coverDoc = await GetCoverDocumentAsync(docCol, setDoc, setId, accessHash);

            var set = new MyTelegram.Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = title,
                ShortName = shortName,
                Count = count,
                Hash = 0
            };

            stickerSetCovers.Add(new TStickerSetCovered
            {
                Set = set,
                Cover = coverDoc
            });
        }

        var totalCount = await setCol.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId));

        logger.LogInformation("GetMyStickers for user {UserId}: {Count} created sets", input.UserId, stickerSetCovers.Count);

        return new TMyStickers
        {
            Count = (int)totalCount,
            Sets = new TVector<IStickerSetCovered>(stickerSetCovers)
        };
    }

    private async Task<IDocument> GetCoverDocumentAsync(IMongoCollection<BsonDocument> docCol, BsonDocument setDoc, long setId, long setAccessHash)
    {
        var docIds = new List<long>();
        if (setDoc.Contains("DocumentIds") && setDoc["DocumentIds"].IsBsonArray)
        {
            docIds = setDoc["DocumentIds"].AsBsonArray.Select(x => GetInt64(x)).ToList();
        }
        
        IDocument cover = new TDocumentEmpty { Id = 0 };
        
        if (docIds.Count > 0)
        {
            var firstDocId = docIds.First();
            var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", firstDocId);
            var docBson = await docCol.Find(docFilter).FirstOrDefaultAsync();
            
            if (docBson != null)
            {
                cover = BuildDocument(docBson, setId, setAccessHash);
            }
        }
        
        return cover;
    }

    private IDocument BuildDocument(BsonDocument docBson, long setId, long setAccessHash)
    {
        var docId = GetInt64(docBson["DocumentId"]);
        var accessHash = GetInt64(docBson["AccessHash"]);
        var mimeType = docBson.Contains("MimeType") ? docBson["MimeType"].AsString : "application/octet-stream";
        var size = GetInt64(docBson["Size"]);
        var dcId = GetInt32(docBson["DcId"]);
        
        byte[] fileRef;
        if (docBson.Contains("FileReference"))
        {
            var fr = docBson["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)GetInt32(x)).ToArray();
            else
                fileRef = [];
        }
        else
        {
            fileRef = [];
        }
        
        return new TDocument
        {
            Id = docId,
            AccessHash = accessHash,
            FileReference = fileRef,
            Date = 0,
            MimeType = mimeType,
            Size = size,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = dcId,
            Attributes = new TVector<IDocumentAttribute>(new[]
            {
                (IDocumentAttribute)new TDocumentAttributeSticker
                {
                    Alt = "",
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = setAccessHash },
                    Mask = false
                }
            })
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
}
