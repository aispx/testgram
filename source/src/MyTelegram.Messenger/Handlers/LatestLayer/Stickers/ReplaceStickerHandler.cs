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

internal sealed class ReplaceStickerHandler(
    IMongoDatabase mongoDatabase,
    ILogger<ReplaceStickerHandler> logger) : RpcResultObjectHandler<Schema.Stickers.RequestReplaceSticker, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, Schema.Stickers.RequestReplaceSticker obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

        if (obj.Sticker is not TInputDocument)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var oldStickerDoc = (TInputDocument)obj.Sticker;
        var oldDocBson = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", oldStickerDoc.Id)).FirstOrDefaultAsync();
        if (oldDocBson == null)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", oldStickerDoc.Id)).FirstOrDefaultAsync();
        if (setDoc == null)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setId = GetInt64(setDoc["StickerSetId"]);

        // Check if user is the creator of the sticker set
        var creatorUserId = setDoc.Contains("CreatorUserId") ? GetInt64(setDoc["CreatorUserId"]) : 0;
        if (creatorUserId != input.UserId)
        {
            logger.LogWarning("User {UserId} tried to replace sticker in set {SetId} created by {CreatorUserId}",
                input.UserId, setId, creatorUserId);
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        if (obj.NewSticker is not TInputStickerSetItem)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        var newSticker = (TInputStickerSetItem)obj.NewSticker;
        long newDocId = 0;
        if (newSticker.Document is TInputDocument inputDoc)
        {
            newDocId = inputDoc.Id;
        }
        else if (newSticker.Document is TInputDocumentEmpty)
        {
            RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
        }

        var newDocBson = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", newDocId)).FirstOrDefaultAsync();
        if (newDocBson == null)
        {
            logger.LogWarning("New sticker document {DocId} not found", newDocId);
            RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
        }

        var accessHash = GetInt64(setDoc["AccessHash"]);

        var docIds = new List<long>();
        if (setDoc.Contains("DocumentIds") && setDoc["DocumentIds"].IsBsonArray)
        {
            docIds = setDoc["DocumentIds"].AsBsonArray.Select(x => GetInt64(x)).ToList();
        }

        var index = docIds.IndexOf(oldStickerDoc.Id);
        if (index < 0)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        docIds[index] = newDocId;
        setDoc["DocumentIds"] = new BsonArray(docIds);

        var packs = new BsonArray();
        if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
        {
            foreach (var p in setDoc["Packs"].AsBsonArray)
            {
                var packDocIds = p["Documents"].AsBsonArray.Select(x => GetInt64(x)).ToList();
                if (packDocIds.Contains(oldStickerDoc.Id))
                {
                    var newPack = p.ToBsonDocument();
                    var docList = packDocIds.ToList();
                    var docIndex = docList.IndexOf(oldStickerDoc.Id);
                    docList[docIndex] = newDocId;
                    newPack["Documents"] = new BsonArray(docList);
                    newPack["Emoticon"] = newSticker.Emoji;
                    packs.Add(newPack);
                }
                else
                {
                    packs.Add(p);
                }
            }
        }
        setDoc["Packs"] = packs;

        await setCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            setDoc);

        logger.LogInformation("Replaced sticker {OldId} with {NewId} in set {SetId}", oldStickerDoc.Id, newDocId, setId);

        var documents = new List<IDocument>();
        if (docIds.Count > 0)
        {
            var docFilter = Builders<BsonDocument>.Filter.In("DocumentId",
                docIds.Select(id => (BsonValue)new BsonInt64(id)));
            var docDocs = await docCol.Find(docFilter).ToListAsync();
            var docMap = docDocs.ToDictionary(d => GetInt64(d["DocumentId"]));

            foreach (var docId in docIds)
            {
                if (docMap.TryGetValue(docId, out var dBson))
                {
                    documents.Add(BuildDocument(dBson, setId, accessHash));
                }
            }
        }

        var stickerPacks = new List<IStickerPack>();
        if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
        {
            foreach (var p in setDoc["Packs"].AsBsonArray)
            {
                stickerPacks.Add(new TStickerPack
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
                Title = setDoc["Title"].AsString,
                ShortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString,
                Count = GetInt32(setDoc["Count"]),
                Hash = 0
            },
            Packs = new TVector<IStickerPack>(stickerPacks),
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
