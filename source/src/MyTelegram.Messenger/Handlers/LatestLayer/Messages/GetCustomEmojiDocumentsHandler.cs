using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers »</a>.Returns a list of <a href="https://corefork.telegram.org/constructor/document">documents</a> with the animated custom emoji in TGS format, and a <a href="https://corefork.telegram.org/constructor/documentAttributeCustomEmoji">documentAttributeCustomEmoji</a> attribute with the original emoji and info about the emoji stickerset this custom emoji belongs to.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getCustomEmojiDocuments"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetCustomEmojiDocumentsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments, TVector<MyTelegram.Schema.IDocument>>
{
    protected override async Task<TVector<MyTelegram.Schema.IDocument>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetCustomEmojiDocuments obj)
    {
        if (obj.DocumentId == null || obj.DocumentId.Count == 0)
        {
            return [];
        }

        Console.WriteLine($"[GetCustomEmojiDocuments] documentIds=[{string.Join(",", obj.DocumentId)}]");

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", obj.DocumentId.Select(x => (BsonValue)new BsonInt64(x)));
        var docs = await docCol.Find(filter).ToListAsync();
        var docMap = docs.ToDictionary(d => GetInt64(d["DocumentId"]));
        var collectibleModelDocumentIds = await GetCollectibleModelDocumentIdsAsync(obj.DocumentId);
        var result = new List<IDocument>();

        foreach (var documentId in obj.DocumentId)
        {
            if (!docMap.TryGetValue(documentId, out var d))
            {
                continue;
            }

            try
            {
                result.Add(BuildDocument(d, collectibleModelDocumentIds.Contains(documentId)));
            }
            catch (Exception ex)
            {
                // Skip malformed documents instead of crashing
                Console.WriteLine($"[GetCustomEmojiDocuments] Failed to build document {documentId}: {ex.Message}");
            }
        }

        return new TVector<IDocument>(result);
    }

    private async Task<HashSet<long>> GetCollectibleModelDocumentIdsAsync(ICollection<long> documentIds)
    {
        var modelAttributeFilter = new BsonDocument
        {
            ["Type"] = "model",
            ["DocumentId"] = new BsonDocument("$in", new BsonArray(documentIds))
        };
        var giftFilter = new BsonDocument("Attributes",
            new BsonDocument("$elemMatch", modelAttributeFilter));

        var gifts = await mongoDatabase.GetCollection<BsonDocument>("unique-star-gifts")
            .Find(giftFilter)
            .Project(Builders<BsonDocument>.Projection.Include("Attributes"))
            .ToListAsync();

        return gifts
            .SelectMany(GetModelDocumentIds)
            .Where(documentIds.Contains)
            .ToHashSet();
    }

    private static IEnumerable<long> GetModelDocumentIds(BsonDocument gift)
    {
        if (!gift.TryGetValue("Attributes", out var attributes) || !attributes.IsBsonArray)
        {
            yield break;
        }

        foreach (var value in attributes.AsBsonArray)
        {
            if (!value.IsBsonDocument)
            {
                continue;
            }

            var attribute = value.AsBsonDocument;
            if (!attribute.TryGetValue("Type", out var type) ||
                !type.IsString ||
                type.AsString != "model" ||
                !attribute.TryGetValue("DocumentId", out var documentId))
            {
                continue;
            }

            yield return GetInt64(documentId);
        }
    }

    private static IDocument BuildDocument(BsonDocument d, bool isCollectibleModelDocument)
    {
        byte[] fileRef = [];
        if (d.Contains("FileReference") && !d["FileReference"].IsBsonNull)
        {
            var fr = d["FileReference"];
            if (fr.BsonType == BsonType.Binary)
                fileRef = fr.AsBsonBinaryData.Bytes;
            else if (fr.BsonType == BsonType.Array)
                fileRef = fr.AsBsonArray.Select(x => (byte)GetInt32(x)).ToArray();
        }

        var attributes = GetValidCustomEmojiAttributes(d, isCollectibleModelDocument);

        return new TDocument
        {
            Id = GetInt64(d["DocumentId"]),
            AccessHash = GetInt64(d["AccessHash"]),
            FileReference = fileRef,
            Date = d.Contains("Date") ? GetInt32(d["Date"]) : 0,
            MimeType = d.Contains("MimeType") ? d["MimeType"].AsString : "application/octet-stream",
            Size = d.Contains("Size") ? GetInt64(d["Size"]) : 0,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = d.Contains("DcId") ? GetInt32(d["DcId"]) : 0,
            Attributes = attributes
        };
    }

    private static TVector<IDocumentAttribute> GetValidCustomEmojiAttributes(BsonDocument d, bool isCollectibleModelDocument)
    {
        if (CustomEmojiAttributeHelper.TryGetCustomEmojiAttribute(d, out var customEmojiAttribute))
        {
            customEmojiAttribute.Stickerset ??= new TInputStickerSetEmpty();
            return [customEmojiAttribute];
        }

        // Emoji statuses may reference regular animated sticker documents
        // (not only documents that were originally stored with
        // documentAttributeCustomEmoji). Clients still fetch them through
        // messages.getCustomEmojiDocuments when resolving the status after
        // reconnect/account reload, so expose the sticker as a custom-emoji
        // document instead of dropping it and causing a stale-status fallback.
        if (CustomEmojiAttributeHelper.TryGetStickerAttributeAsCustomEmoji(d, out var stickerAsCustomEmoji))
        {
            stickerAsCustomEmoji.Stickerset ??= new TInputStickerSetEmpty();
            return [stickerAsCustomEmoji];
        }

        // Unique gift models are valid collectible emoji-status documents even
        // when their source document was uploaded before Attributes2 existed.
        if (isCollectibleModelDocument)
        {
            return
            [
                new TDocumentAttributeCustomEmoji
                {
                    Alt = "🎁",
                    Free = true,
                    Stickerset = new TInputStickerSetEmpty()
                }
            ];
        }

        if (!d.Contains("Attributes2") || d["Attributes2"].IsBsonNull)
        {
            throw new InvalidDataException("Missing custom emoji attributes.");
        }

        throw new InvalidDataException("Document is not a custom emoji.");
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => 0
        };
    }

    private static int GetInt32(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int32 => v.AsInt32,
            BsonType.Int64 => (int)v.AsInt64,
            BsonType.Double => (int)v.AsDouble,
            _ => 0
        };
    }
}
