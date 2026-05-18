using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Stickers;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeCustomEmoji = MyTelegram.Schema.TDocumentAttributeCustomEmoji;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class ChangeStickerHandler(
    IMongoDatabase mongoDatabase,
    ILogger<ChangeStickerHandler> logger) : RpcResultObjectHandler<Schema.Stickers.RequestChangeSticker, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, Schema.Stickers.RequestChangeSticker obj)
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

        var setDoc = await setCol.Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", stickerDoc.Id)).FirstOrDefaultAsync();
        if (setDoc == null)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var setId = GetInt64(setDoc["StickerSetId"]);

        // Check if user is the creator of the sticker set
        var creatorUserId = setDoc.Contains("CreatorUserId") ? GetInt64(setDoc["CreatorUserId"]) : 0;
        if (creatorUserId != input.UserId)
        {
            logger.LogWarning("User {UserId} tried to change sticker in set {SetId} created by {CreatorUserId}",
                input.UserId, setId, creatorUserId);
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        var accessHash = GetInt64(setDoc["AccessHash"]);
        var isEmojiSet = setDoc.Contains("Emojis") && setDoc["Emojis"].ToBoolean();
        var textColor = setDoc.Contains("TextColor") && setDoc["TextColor"].ToBoolean();

        if (!string.IsNullOrEmpty(obj.Emoji))
        {
            var packs = new BsonArray();
            if (setDoc.Contains("Packs") && setDoc["Packs"].IsBsonArray)
            {
                foreach (var p in setDoc["Packs"].AsBsonArray)
                {
                    var packDocIds = p["Documents"].AsBsonArray.Select(x => GetInt64(x)).ToList();
                    if (packDocIds.Contains(stickerDoc.Id))
                    {
                        var newPack = p.ToBsonDocument();
                        newPack["Emoticon"] = obj.Emoji;
                        packs.Add(newPack);
                    }
                    else
                    {
                        packs.Add(p);
                    }
                }
            }
            setDoc["Packs"] = packs;
            logger.LogInformation("Changed sticker {DocId} emoji to '{Emoji}'", stickerDoc.Id, obj.Emoji);
        }

        await setCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", setId),
            setDoc);

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
                if (docMap.TryGetValue(docId, out var dBson))
                {
                    documents.Add(BuildDocument(dBson, setId, accessHash, isEmojiSet, textColor, GetAltForDocument(setDoc, docId)));
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
                Hash = 0,
                Emojis = isEmojiSet,
                TextColor = textColor
            },
            Packs = new TVector<IStickerPack>(stickerPacks),
            Documents = new TVector<IDocument>(documents),
            Keywords = new TVector<IStickerKeyword>()
        };
    }

    private IDocument BuildDocument(BsonDocument docBson, long setId, long setAccessHash, bool isEmojiSet, bool textColor, string alt)
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
            Attributes = new TVector<IDocumentAttribute>(new IDocumentAttribute[]
            {
                BuildPrimaryAttribute(isEmojiSet, textColor, alt, setId, setAccessHash)
            })
        };
    }

    private static IDocumentAttribute BuildPrimaryAttribute(bool isEmojiSet, bool textColor, string alt, long setId, long accessHash)
    {
        return isEmojiSet
            ? new TDocumentAttributeCustomEmoji
            {
                Alt = alt,
                Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                Free = true,
                TextColor = textColor
            }
            : new TDocumentAttributeSticker
            {
                Alt = alt,
                Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                Mask = false
            };
    }

    private static string GetAltForDocument(BsonDocument setDoc, long documentId)
    {
        if (!setDoc.Contains("Packs") || !setDoc["Packs"].IsBsonArray)
        {
            return string.Empty;
        }

        foreach (var pack in setDoc["Packs"].AsBsonArray.Select(x => x.AsBsonDocument))
        {
            if (pack.Contains("Documents") && pack["Documents"].AsBsonArray.Any(x => GetInt64(x) == documentId))
            {
                return pack.Contains("Emoticon") ? pack["Emoticon"].AsString : string.Empty;
            }
        }

        return string.Empty;
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
