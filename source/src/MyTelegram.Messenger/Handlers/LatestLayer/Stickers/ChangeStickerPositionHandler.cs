using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Stickers;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class ChangeStickerPositionHandler(
    IMongoDatabase mongoDatabase,
    ILogger<ChangeStickerPositionHandler> logger) : RpcResultObjectHandler<Schema.Stickers.RequestChangeStickerPosition, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, Schema.Stickers.RequestChangeStickerPosition obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        if (obj.Sticker is not TInputDocument)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var stickerDoc = (TInputDocument)obj.Sticker;
        var docBson = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", stickerDoc.Id)).FirstOrDefaultAsync();
        if (docBson == null)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var setId = stickerDoc.Id;
        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", setId)).FirstOrDefaultAsync();
        if (setDoc == null)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var docIds = new List<long>();
        if (setDoc.Contains("DocumentIds") && setDoc["DocumentIds"].IsBsonArray)
        {
            docIds = setDoc["DocumentIds"].AsBsonArray.Select(x => GetInt64(x)).ToList();
        }

        var oldIndex = docIds.IndexOf(stickerDoc.Id);
        if (oldIndex < 0)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        docIds.RemoveAt(oldIndex);
        var newIndex = Math.Min(Math.Max(0, obj.Position), docIds.Count);
        docIds.Insert(newIndex, stickerDoc.Id);

        setDoc["DocumentIds"] = new BsonArray(docIds);

        await setCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            setDoc);

        logger.LogInformation("Moved sticker {DocId} to position {Position} in set {SetId}", stickerDoc.Id, obj.Position, setId);

        return await BuildStickerSetResponseAsync(setCol, docCol, setDoc);
    }

    private async Task<Schema.Messages.IStickerSet> BuildStickerSetResponseAsync(
        IMongoCollection<BsonDocument> setCol,
        IMongoCollection<BsonDocument> docCol,
        BsonDocument setDoc)
    {
        var setId = GetInt64(setDoc["StickerSetId"]);
        var accessHash = GetInt64(setDoc["AccessHash"]);
        var title = setDoc["Title"].AsString;
        var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
        var count = GetInt32(setDoc["Count"]);

        var docIds = new List<long>();
        if (setDoc.Contains("DocumentIds") && setDoc["DocumentIds"].IsBsonArray)
        {
            docIds = setDoc["DocumentIds"].AsBsonArray.Select(x => GetInt64(x)).ToList();
        }

        var documents = new List<IDocument>();
        if (docIds.Count > 0)
        {
            var docFilter = Builders<BsonDocument>.Filter.In("DocumentId",
                docIds.Select(id => (BsonValue)new BsonInt64(id)));
            var docDocs = await docCol.Find(docFilter).ToListAsync();
            var docMap = docDocs.ToDictionary(d => GetInt64(d["DocumentId"]));

            foreach (var docId in docIds)
            {
                if (docMap.TryGetValue(docId, out var docBson))
                {
                    documents.Add(BuildDocument(docBson, setId, accessHash));
                }
            }
        }

        var packs = new List<IStickerPack>();
        if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
        {
            foreach (var p in setDoc["Packs"].AsBsonArray)
            {
                packs.Add(new TStickerPack
                {
                    Emoticon = p["Emoticon"].AsString,
                    Documents = new TVector<long>(p["Documents"].AsBsonArray.Select(x => GetInt64(x)).ToList())
                });
            }
        }

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
            Packs = new TVector<IStickerPack>(packs),
            Documents = new TVector<IDocument>(documents),
            Keywords = []
        };
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
            Thumbs = [],
            VideoThumbs = [],
            DcId = dcId,
            Attributes = new TVector<IDocumentAttribute>(new IDocumentAttribute[]
            {
                new TDocumentAttributeSticker
                {
                    Alt = "",
                    Stickerset = new TInputStickerSetID { Id = setId, AccessHash = setAccessHash },
                    Mask = false
                }
            })
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
