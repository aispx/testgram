using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.WallPapers;

/// <inheritdoc />
public sealed class WallPaperCatalog(
    IMongoDatabase database,
    IFileReferenceHelper fileReferenceHelper,
    ILogger<WallPaperCatalog> logger) : IWallPaperCatalog, ITransientDependency
{
    private const string CollectionName = "wallpapers";
    private const string DocumentCollectionName = "eventflow-documentreadmodel";
    private const string CounterId = "wallpaper_id";

    /// <summary>
    /// Length of the random part of a <c>slug</c>. The slug is public — it is what a
    /// <a href="https://corefork.telegram.org/api/links#wallpaper-links">wallpaper link</a> carries — so it
    /// must not be derived from the id the way <c>custom_{id}</c> was.
    /// </summary>
    private const int SlugBytes = 15;

    public async Task<WallPaperRow?> FindByIdAsync(long wallPaperId)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("WallpaperId", wallPaperId))
            .FirstOrDefaultAsync();

        return doc == null ? null : ToRow(doc);
    }

    /// <summary>
    /// The wallpaper a <a href="https://corefork.telegram.org/api/links#wallpaper-links">wallpaper
    /// link</a> names. <b>A slug is not unique</b>: it identifies the pattern image, and real Telegram
    /// serves six of them two or three times over — the same pattern recoloured light/dark or with
    /// different fill colours (measured). The lowest catalogue order wins, so a link always opens the
    /// same variant.
    /// </summary>
    public async Task<WallPaperRow?> FindBySlugAsync(string slug)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("Slug", slug))
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("WallpaperId"))
            .FirstOrDefaultAsync();

        return doc == null ? null : ToRow(doc);
    }

    public async Task<List<WallPaperRow>> FindManyAsync(IReadOnlyCollection<long> wallPaperIds,
        IReadOnlyCollection<string> slugs)
    {
        if (wallPaperIds.Count == 0 && slugs.Count == 0)
        {
            return [];
        }

        var filters = new List<FilterDefinition<BsonDocument>>();
        if (wallPaperIds.Count > 0)
        {
            filters.Add(Builders<BsonDocument>.Filter.In("WallpaperId", wallPaperIds));
        }

        if (slugs.Count > 0)
        {
            filters.Add(Builders<BsonDocument>.Filter.In("Slug", slugs));
        }

        var docs = await Collection.Find(Builders<BsonDocument>.Filter.Or(filters)).ToListAsync();

        return docs.ConvertAll(ToRow);
    }

    public async Task<List<WallPaperRow>> GetListedAsync()
    {
        // `Listed` is what puts a wallpaper in every account's starting list; `IsDefault` is the wire flag
        // and real Telegram sets it on only 76 of the 83 wallpapers it lists. Rows written before `Listed`
        // existed carry only `IsDefault`, and are read as listed — see ToRow.
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Eq("Listed", true),
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Exists("Listed", false),
                Builders<BsonDocument>.Filter.Eq("IsDefault", true)));

        var docs = await Collection.Find(filter).ToListAsync();

        return [.. docs.ConvertAll(ToRow).OrderBy(p => p.Order).ThenBy(p => p.WallPaperId)];
    }

    public async Task<MyTelegram.Schema.IWallPaper?> BuildAsync(WallPaperRow row, long selfUserId,
        MyTelegram.Schema.IWallPaperSettings? settings = null)
    {
        var effectiveSettings = settings ?? row.Settings;

        if (row.IsFill)
        {
            return BuildFill(row.WallPaperId, effectiveSettings, row.IsDark);
        }

        var documentDoc = await database.GetCollection<BsonDocument>(DocumentCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", row.DocumentId))
            .FirstOrDefaultAsync();

        if (documentDoc == null)
        {
            // Leaving it out is right — a wallpaper whose document is gone renders as nothing — but it is
            // also the only way to lose the whole catalogue in silence, which is what happened when the
            // importer wrote a DocumentId taken from real Telegram without importing the file behind it.
            logger.LogWarning(
                "Wallpaper {WallPaperId} ({Slug}) names document {DocumentId}, which has no row: leaving it out of the response",
                row.WallPaperId, row.Slug, row.DocumentId);

            return null;
        }

        return new MyTelegram.Schema.TWallPaper
        {
            Id = row.WallPaperId,
            AccessHash = row.AccessHash,
            Slug = row.Slug,
            // "creator" means "the caller made this one", not a property of the wallpaper: Android copies
            // the flag when applying a wallpaper it received, and it used to be dropped on every path.
            Creator = row.CreatedBy != 0 && row.CreatedBy == selfUserId,
            Default = row.IsDefault,
            Pattern = row.IsPattern,
            Dark = row.IsDark,
            Document = ToDocument(documentDoc),
            Settings = effectiveSettings
        };
    }

    public MyTelegram.Schema.IWallPaper BuildFill(long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings, bool dark = false)
    {
        return new MyTelegram.Schema.TWallPaperNoFile
        {
            Id = wallPaperId,
            Dark = dark,
            Settings = settings
        };
    }

    public async Task<WallPaperRow> InsertUploadedAsync(long creatorUserId, long documentId, string mimeType,
        bool pattern, bool forChat, MyTelegram.Schema.IWallPaperSettings? settings)
    {
        var wallPaperId = await NextIdAsync();
        var row = new WallPaperRow(
            wallPaperId,
            NewAccessHash(),
            NewSlug(),
            documentId,
            IsDefault: false,
            IsPattern: pattern,
            IsDark: false,
            ForChat: forChat,
            // A user's own upload reaches their list through user_wallpapers, not everybody else's.
            Listed: false,
            CreatedBy: creatorUserId,
            Order: 0,
            Settings: settings);

        await Collection.InsertOneAsync(new BsonDocument
        {
            { "_id", $"wallpaper-{wallPaperId}" },
            { "WallpaperId", row.WallPaperId },
            { "AccessHash", row.AccessHash },
            { "Slug", row.Slug },
            { "DocumentId", row.DocumentId },
            { "MimeType", mimeType },
            { "IsDefault", false },
            { "IsPattern", row.IsPattern },
            { "IsDark", false },
            { "ForChat", row.ForChat },
            { "Listed", false },
            { "CreatedBy", row.CreatedBy },
            { "Order", 0 },
            { "Settings", WallPaperSettingsSerializer.ToBson(settings) },
            { "CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        });

        return row;
    }

    private IMongoCollection<BsonDocument> Collection => database.GetCollection<BsonDocument>(CollectionName);

    private async Task<long> NextIdAsync()
    {
        var result = await database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", CounterId),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

        // Above the ids the seeded catalogue uses, and always positive: Android drops a wallpaper with a
        // negative id when it folds the list hash, so a negative id would make its hash unmatchable.
        return 2_000_000_000_000L + result["seq"].ToInt64();
    }

    private static long NewAccessHash()
    {
        return BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
    }

    private static string NewSlug()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(SlugBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static WallPaperRow ToRow(BsonDocument doc)
    {
        var isDefault = doc.GetValue("IsDefault", false).ToBoolean();

        return new WallPaperRow(
            doc.GetValue("WallpaperId", 0L).ToInt64(),
            doc.GetValue("AccessHash", 0L).ToInt64(),
            doc.GetValue("Slug", string.Empty).AsString,
            doc.GetValue("DocumentId", 0L).ToInt64(),
            isDefault,
            doc.GetValue("IsPattern", false).ToBoolean(),
            doc.GetValue("IsDark", false).ToBoolean(),
            doc.GetValue("ForChat", false).ToBoolean(),
            // Rows seeded before Listed existed used IsDefault to mean both things.
            doc.GetValue("Listed", isDefault).ToBoolean(),
            doc.GetValue("CreatedBy", 0L).ToInt64(),
            doc.GetValue("Order", 0).ToInt32(),
            WallPaperSettingsSerializer.FromBson(doc.GetValue("Settings", BsonNull.Value)));
    }

    private MyTelegram.Schema.IDocument ToDocument(BsonDocument doc)
    {
        var documentId = doc["DocumentId"].ToInt64();

        return new MyTelegram.Schema.TDocument
        {
            Id = documentId,
            AccessHash = doc.GetValue("AccessHash", 0L).ToInt64(),
            // Wallpaper rows carry no stored FileReference, so this used to go out empty — something the
            // official server never serves. See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = doc.GetValue("Date", 0).ToInt32(),
            MimeType = doc.GetValue("MimeType", "image/jpeg").AsString,
            Size = doc.GetValue("Size", 0L).ToInt64(),
            Thumbs = ToThumbs(doc),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = doc.Contains("DcId") && doc["DcId"].ToInt32() > 0
                ? doc["DcId"].ToInt32()
                : MyTelegramConsts.MediaDcId,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        };
    }

    /// <summary>
    /// The grid tile is drawn from a thumbnail: Android asks for the closest size to 320
    /// (<c>MessagesController.uploadWallpaper</c>) and falls back to the full file when there is none.
    /// </summary>
    private static TVector<MyTelegram.Schema.IPhotoSize> ToThumbs(BsonDocument doc)
    {
        var thumbs = new TVector<MyTelegram.Schema.IPhotoSize>();

        if (doc.GetValue("Thumbs", BsonNull.Value) is not BsonArray stored)
        {
            return thumbs;
        }

        foreach (var value in stored.OfType<BsonDocument>())
        {
            thumbs.Add(new MyTelegram.Schema.TPhotoSize
            {
                Type = value.GetValue("Type", "m").AsString,
                W = value.GetValue("W", 0).ToInt32(),
                H = value.GetValue("H", 0).ToInt32(),
                Size = value.GetValue("Size", 0).ToInt32()
            });
        }

        return thumbs;
    }
}
