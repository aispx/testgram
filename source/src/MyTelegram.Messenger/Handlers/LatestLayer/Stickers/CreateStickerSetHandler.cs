using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Stickers;
using TDocument = MyTelegram.Schema.TDocument;
using TDocumentAttributeSticker = MyTelegram.Schema.TDocumentAttributeSticker;
using TInputStickerSetID = MyTelegram.Schema.TInputStickerSetID;
using TStickerSet = MyTelegram.Schema.Messages.TStickerSet;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

internal sealed class CreateStickerSetHandler(
    IMongoDatabase mongoDatabase,
    ILogger<CreateStickerSetHandler> logger) : RpcResultObjectHandler<RequestCreateStickerSet, Schema.Messages.IStickerSet>
{
    protected override async Task<Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input, RequestCreateStickerSet obj)
    {
        Console.WriteLine($"[DEBUG] CreateStickerSetHandler called: Title={obj.Title}, ShortName={obj.ShortName}, Stickers={obj.Stickers.Count}, UserId={input.UserId}");
        if (string.IsNullOrWhiteSpace(obj.Title))
        {
            RpcErrors.RpcErrors400.PackTitleInvalid.ThrowRpcError();
        }

        var setCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");

        // Auto-generate ShortName from Title if not provided
        if (string.IsNullOrWhiteSpace(obj.ShortName))
        {
            obj.ShortName = await GenerateUniqueShortNameAsync(obj.Title, setCol);
        }

        if (obj.Stickers.Count == 0)
        {
            RpcErrors.RpcErrors400.StickersEmpty.ThrowRpcError();
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(obj.ShortName, @"^[a-zA-Z][a-zA-Z0-9_]{0,63}$"))
        {
            RpcErrors.RpcErrors400.PackShortNameInvalid.ThrowRpcError();
        }

        var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var userSetCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userinstalledstickersetreadmodel");

        var existingSet = await setCol.Find(Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("ShortName", obj.ShortName),
            Builders<BsonDocument>.Filter.Eq("Slug", obj.ShortName)
        )).FirstOrDefaultAsync();

        if (existingSet != null)
        {
            RpcErrors.RpcErrors400.PackShortNameOccupied.ThrowRpcError();
        }

        var setId = GenerateId();
        var accessHash = GenerateAccessHash();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var documentIds = new List<long>();
        var packs = new BsonArray();
        var documents = new List<IDocument>();

        foreach (var stickerItem in obj.Stickers)
        {
            if (stickerItem is not TInputStickerSetItem sticker)
            {
                continue;
            }

            long docId = 0;
            long docAccessHash = 0;

            if (sticker.Document is TInputDocument inputDoc)
            {
                docId = inputDoc.Id;
                docAccessHash = inputDoc.AccessHash;
            }
            else if (sticker.Document is TInputDocumentEmpty)
            {
                RpcErrors.RpcErrors400.StickerFileInvalid.ThrowRpcError();
            }

            var existingDoc = await docCol.Find(Builders<BsonDocument>.Filter.Eq("DocumentId", docId)).FirstOrDefaultAsync();
            if (existingDoc == null)
            {
                logger.LogWarning("Sticker document {DocId} not found when creating sticker set", docId);
                continue;
            }

            documentIds.Add(docId);

            packs.Add(new BsonDocument
            {
                ["Emoticon"] = sticker.Emoji,
                ["Documents"] = new BsonArray(new[] { (BsonValue)docId })
            });

            var mimeType = existingDoc.Contains("MimeType") ? existingDoc["MimeType"].AsString : "application/octet-stream";
            var size = GetInt64(existingDoc["Size"]);
            var dcId = GetInt32(existingDoc["DcId"]);

            byte[] fileRef;
            if (existingDoc.Contains("FileReference"))
            {
                var fr = existingDoc["FileReference"];
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

            documents.Add(new TDocument
            {
                Id = docId,
                AccessHash = docAccessHash,
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
                        Stickerset = new TInputStickerSetID { Id = setId, AccessHash = accessHash },
                        Mask = obj.Masks
                    }
                })
            });
        }

        var newSetDoc = new BsonDocument
        {
            ["_id"] = $"stickersetreadmodel-{setId}",
            ["Id"] = $"stickersetreadmodel-{setId}",
            ["StickerSetId"] = setId,
            ["AccessHash"] = accessHash,
            ["ShortName"] = obj.ShortName,
            ["Title"] = obj.Title,
            ["Slug"] = obj.ShortName,
            ["Count"] = documentIds.Count,
            ["DocumentIds"] = new BsonArray(documentIds),
            ["Packs"] = packs,
            ["Version"] = 1
        };

        await setCol.InsertOneAsync(newSetDoc);

        await userSetCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId().ToString(),
            ["Id"] = ObjectId.GenerateNewId().ToString(),
            ["UserId"] = input.UserId,
            ["StickerSetId"] = setId,
            ["AccessHash"] = accessHash,
            ["ShortName"] = obj.ShortName,
            ["Title"] = obj.Title,
            ["Archived"] = false,
            ["InstalledAt"] = now,
            ["Version"] = 1
        });

        logger.LogInformation("Created sticker set {SetId} ({ShortName}) with {Count} stickers for user {UserId}",
            setId, obj.ShortName, documentIds.Count, input.UserId);

        var stickerPacks = new List<IStickerPack>();
        foreach (var p in packs.ToList())
        {
            stickerPacks.Add(new TStickerPack
            {
                Emoticon = p["Emoticon"].AsString,
                Documents = new TVector<long>(p["Documents"].AsBsonArray.Select(x => GetInt64(x)).ToList())
            });
        }

        return new TStickerSet
        {
            Set = new Schema.TStickerSet
            {
                Id = setId,
                AccessHash = accessHash,
                Title = obj.Title,
                ShortName = obj.ShortName,
                Count = documentIds.Count,
                Hash = 0
            },
            Packs = new TVector<IStickerPack>(stickerPacks),
            Documents = new TVector<IDocument>(documents),
            Keywords = []
        };
    }

    private static long GenerateId()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        bytes[0] &= 0x7F;
        return BitConverter.ToInt64(bytes, 0) & 0x7FFFFFFFFFFFFFFF;
    }

    private static long GenerateAccessHash()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        return BitConverter.ToInt64(bytes, 0);
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

    private static async Task<string> GenerateUniqueShortNameAsync(string title, IMongoCollection<BsonDocument> setCol)
    {
        // Generate base short_name from title
        var baseShortName = new string(title
            .Replace(' ', '_')
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .Take(32)
            .ToArray());

        // Remove leading/trailing underscores
        baseShortName = baseShortName.Trim('_');

        // If empty after filtering, use default
        if (string.IsNullOrEmpty(baseShortName))
        {
            baseShortName = "sticker";
        }

        // First try without suffix
        var shortName = baseShortName;
        var exists = await setCol.Find(Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("ShortName", shortName),
            Builders<BsonDocument>.Filter.Eq("Slug", shortName)
        )).AnyAsync();

        if (!exists)
        {
            return shortName;
        }

        // If taken, try with random 3-digit suffix
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var suffix = Random.Shared.Next(100, 999);
            shortName = baseShortName + suffix;

            exists = await setCol.Find(Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Eq("ShortName", shortName),
                Builders<BsonDocument>.Filter.Eq("Slug", shortName)
            )).AnyAsync();

            if (!exists)
            {
                return shortName;
            }
        }

        // Fallback: use timestamp
        shortName = baseShortName + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return shortName;
    }
}
