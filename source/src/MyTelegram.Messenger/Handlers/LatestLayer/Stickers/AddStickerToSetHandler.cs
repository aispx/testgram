using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Stickers;
using IStickerSet = MyTelegram.Schema.Messages.IStickerSet;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class AddStickerToSetHandler(
    IMongoDatabase mongoDatabase,
    ILogger<AddStickerToSetHandler> logger) : RpcResultObjectHandler<RequestAddStickerToSet, IStickerSet>
{
    protected override async Task<IStickerSet> HandleCoreAsync(IRequestInput input, RequestAddStickerToSet obj)
    {
        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

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
            logger.LogWarning("Stickerset not found: {Type}", obj.Stickerset.GetType().Name);
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setId = GetInt64(setDoc["StickerSetId"]);
        var accessHash = GetInt64(setDoc["AccessHash"]);
        var title = setDoc["Title"].AsString;
        var shortName = setDoc.Contains("ShortName") ? setDoc["ShortName"].AsString : setDoc["Slug"].AsString;
        
        var docIds = new List<long>();
        if (setDoc.Contains("DocumentIds") && setDoc["DocumentIds"].IsBsonArray)
        {
            docIds = setDoc["DocumentIds"].AsBsonArray.Select(x => GetInt64(x)).ToList();
        }

        var sticker = obj.Sticker;
        if (sticker is IInputStickerSetItem stickerItem)
        {
            long docId = 0;
            long docAccessHash = 0;

            if (stickerItem.Document is TInputDocument inputDoc)
            {
                docId = inputDoc.Id;
                docAccessHash = inputDoc.AccessHash;
            }
            else if (stickerItem.Document is TInputDocumentEmpty)
            {
                RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
            }

            if (docIds.Contains(docId))
            {
                logger.LogWarning("Sticker {DocId} already in set {SetId}", docId, setId);
                return await BuildStickerSetResponseAsync(setDoc, setId, accessHash);
            }

            var existingDoc = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", docId)).FirstOrDefaultAsync();
            if (existingDoc == null)
            {
                logger.LogWarning("Sticker document {DocId} not found", docId);
                RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
            }

            // Update document with stickerset information in Attributes2
            var stickerAttribute = new TDocumentAttributeSticker
            {
                Alt = stickerItem.Emoji,
                Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                Mask = false
            };

            var attributes2List = new List<IDocumentAttribute> { stickerAttribute };

            // Preserve existing attributes if any
            if (existingDoc.Contains("Attributes2") && existingDoc["Attributes2"] != BsonNull.Value)
            {
                try
                {
                    // Keep non-sticker attributes
                    var existingAttrs = BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(existingDoc["Attributes2"].ToJson());
                    attributes2List.AddRange(existingAttrs.Where(a => a is not TDocumentAttributeSticker));
                }
                catch
                {
                    // Ignore deserialization errors
                }
            }

            // Serialize properly using BsonSerializer
            var attributes2Json = System.Text.Json.JsonSerializer.Serialize(new TVector<IDocumentAttribute>(attributes2List));
            var attributes2Bson = BsonSerializer.Deserialize<BsonDocument>(attributes2Json);

            var updateDoc = Builders<BsonDocument>.Update.Set("Attributes2", attributes2Bson);
            await docCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("DocumentId", docId),
                updateDoc
            );

            docIds.Add(docId);
            setDoc["DocumentIds"] = new BsonArray(docIds);
            setDoc["Count"] = docIds.Count;

            var packs = new BsonArray();
            if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
            {
                packs = new BsonArray(setDoc["Packs"].AsBsonArray);
            }

            packs.Add(new BsonDocument
            {
                ["Emoticon"] = stickerItem.Emoji,
                ["Documents"] = new BsonArray(new[] { (BsonValue)docId })
            });
            setDoc["Packs"] = packs;

            await setCol.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
                setDoc);

            logger.LogInformation("Added sticker {DocId} to set {SetId} with emoji {Emoji}", 
                docId, setId, stickerItem.Emoji);
        }

        return await BuildStickerSetResponseAsync(setDoc, setId, accessHash);
    }

    private async Task<IStickerSet> BuildStickerSetResponseAsync(BsonDocument setDoc, long setId, long accessHash)
    {
        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        
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
