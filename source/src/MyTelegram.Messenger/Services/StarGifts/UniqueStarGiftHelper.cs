using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class UniqueStarGiftHelper
{
    public static TStarGiftUnique ToTl(UniqueStarGiftDocument doc)
    {
        var attrs = new TVector<IStarGiftAttribute>();
        foreach (var a in doc.Attributes)
        {
            IStarGiftAttribute attr = a.Type switch
            {
                "backdrop" => new TStarGiftAttributeBackdrop
                {
                    Name = a.Name,
                    BackdropId = a.BackdropId ?? 0,
                    CenterColor = a.CenterColor ?? 0,
                    EdgeColor = a.EdgeColor ?? 0,
                    PatternColor = a.PatternColor ?? 0,
                    TextColor = a.TextColor ?? 0,
                    RarityPermille = a.RarityPermille,
                },
                "pattern" => new TStarGiftAttributePattern
                {
                    Name = a.Name,
                    Document = MakeDoc(a),
                    RarityPermille = a.RarityPermille,
                },
                _ => new TStarGiftAttributeModel
                {
                    Name = a.Name,
                    Document = MakeDoc(a),
                    RarityPermille = a.RarityPermille,
                },
            };
            attrs.Add(attr);
        }

        // Add original details attribute
        var originalRecipientId = doc.OriginalRecipientUserId > 0 ? doc.OriginalRecipientUserId : doc.OwnerUserId;
        attrs.Add(new TStarGiftAttributeOriginalDetails
        {
            RecipientId = originalRecipientId > 0
                ? (IPeer)new TPeerUser { UserId = originalRecipientId }
                : new TPeerChannel { ChannelId = doc.OwnerChannelId },
            SenderId = doc.NameHidden || doc.FromUserId == 0 ? null : new TPeerUser { UserId = doc.FromUserId },
            Date = doc.Date,
            Message = doc.MessageText != null ? new TTextWithEntities { Text = doc.MessageText, Entities = [] } : null,
        });

        return new TStarGiftUnique
        {
            Id = doc.UniqueId,
            GiftId = doc.GiftId,
            Title = doc.Title,
            Slug = doc.Slug,
            Num = doc.Num,
            OwnerId = doc.OwnerUserId > 0
                ? (IPeer)new TPeerUser { UserId = doc.OwnerUserId }
                : new TPeerChannel { ChannelId = doc.OwnerChannelId },
            Attributes = attrs,
            AvailabilityIssued = doc.AvailabilityIssued,
            AvailabilityTotal = doc.AvailabilityTotal,
            ResellAmount = doc.ResellStars > 0
                ? new TVector<IStarsAmount> { new TStarsAmount { Amount = doc.ResellStars, Nanos = 0 } }
                : null,
            ValueAmount = doc.InitialSaleStars > 0 ? doc.InitialSaleStars : null,
            ValueCurrency = doc.InitialSaleStars > 0 ? "XTR" : null,
            ValueUsdAmount = doc.InitialSaleStars > 0 ? doc.InitialSaleStars : null,
            OfferMinStars = doc.OfferMinStars > 0 ? doc.OfferMinStars : null,
        };
    }

    private static IDocument MakeDoc(UniqueGiftAttribute a) => new TDocument
    {
        Id = a.DocumentId ?? 0,
        AccessHash = a.DocumentAccessHash ?? 0,
        FileReference = a.FileReference ?? [],
        Date = a.DocumentDate ?? 0,
        MimeType = a.MimeType ?? "application/x-tgsticker",
        Size = a.DocumentSize ?? 0,
        DcId = a.DcId ?? 2,
        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
    };

    public static async Task<long> NextUniqueIdAsync(IMongoDatabase db)
    {
        var counter = await db.GetCollection<MongoDB.Bson.BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", "unique-star-gift"),
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Inc("seq", 1L),
                new FindOneAndUpdateOptions<MongoDB.Bson.BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }
            );
        return counter["seq"].AsInt64;
    }

    public static async Task<int> NextNumForGiftAsync(IMongoDatabase db, long giftId)
    {
        var counter = await db.GetCollection<MongoDB.Bson.BsonDocument>("counters")
            .FindOneAndUpdateAsync(
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", $"unique-gift-num-{giftId}"),
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Inc("seq", 1),
                new FindOneAndUpdateOptions<MongoDB.Bson.BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }
            );
        return counter["seq"].AsInt32;
    }

    // Generate random attributes from DB config, fallback to gift sticker if no config
    public static async Task<UniqueGiftAttribute[]> GenerateAttributesAsync(IMongoDatabase db, StarGiftDocument gift)
    {
        var col = db.GetCollection<UpgradeConfigEntry>("star-gift-upgrade-config");
        var all = await col.Find(Builders<UpgradeConfigEntry>.Filter.In("gift_id", new[] { gift.GiftId, 0L })).ToListAsync();

        var models    = all.Where(e => e.Type == "model"   && e.GiftId == gift.GiftId).ToList();
        if (models.Count == 0) models = all.Where(e => e.Type == "model" && e.GiftId == 0).ToList();
        var patterns  = all.Where(e => e.Type == "pattern" && e.GiftId == gift.GiftId).ToList();
        if (patterns.Count == 0) patterns = all.Where(e => e.Type == "pattern" && e.GiftId == 0).ToList();
        var backdrops = all.Where(e => e.Type == "backdrop").ToList();

        // Fallback: use gift sticker as model/pattern if no config
        UniqueGiftAttribute Pick(List<UpgradeConfigEntry> list, string type)
        {
            if (list.Count == 0)
                return new UniqueGiftAttribute
                {
                    Type = type, Name = gift.Title ?? "Gift",
                    RarityPermille = 100,
                    DocumentId = gift.DocumentId, DocumentAccessHash = gift.DocumentAccessHash,
                    FileReference = gift.FileReference, DocumentDate = gift.DocumentDate,
                    MimeType = gift.MimeType, DocumentSize = gift.DocumentSize, DcId = gift.DcId,
                };
            var e = WeightedRandom(list);
            return new UniqueGiftAttribute
            {
                Type = type, Name = e.Name, RarityPermille = e.RarityPermille,
                DocumentId = e.DocumentId, DocumentAccessHash = e.DocumentAccessHash,
                FileReference = e.FileReference, DocumentDate = e.DocumentDate,
                MimeType = e.MimeType, DocumentSize = e.DocumentSize, DcId = e.DcId,
            };
        }

        UniqueGiftAttribute PickBackdrop()
        {
            if (backdrops.Count == 0)
                return new UniqueGiftAttribute { Type = "backdrop", Name = "Default", RarityPermille = 100, BackdropId = 1, CenterColor = 0x2980B9, EdgeColor = 0x1A5276, PatternColor = 0x3498DB, TextColor = 0xFFFFFF };
            var e = WeightedRandom(backdrops);
            return new UniqueGiftAttribute
            {
                Type = "backdrop", Name = e.Name, RarityPermille = e.RarityPermille,
                BackdropId = e.BackdropId, CenterColor = e.CenterColor,
                EdgeColor = e.EdgeColor, PatternColor = e.PatternColor, TextColor = e.TextColor,
            };
        }

        return [Pick(models, "model"), Pick(patterns, "pattern"), PickBackdrop()];
    }

    private static T WeightedRandom<T>(List<T> items) where T : UpgradeConfigEntry
    {
        var total = items.Sum(e => e.RarityPermille);
        var r = Random.Shared.Next(total);
        var acc = 0;
        foreach (var e in items) { acc += e.RarityPermille; if (r < acc) return e; }
        return items[^1];
    }

    // Sync fallback (used by preview when no DB available)
    public static UniqueGiftAttribute[] GenerateAttributes(StarGiftDocument gift)
    {
        var rng = Random.Shared;
        var backdrops = new[]
        {
            new { Name = "Crimson", BackdropId = 1, Center = 0xC0392B, Edge = 0x922B21, Pattern = 0xE74C3C, Text = 0xFFFFFF },
            new { Name = "Azure",   BackdropId = 2, Center = 0x2980B9, Edge = 0x1A5276, Pattern = 0x3498DB, Text = 0xFFFFFF },
            new { Name = "Emerald", BackdropId = 3, Center = 0x27AE60, Edge = 0x1E8449, Pattern = 0x2ECC71, Text = 0xFFFFFF },
            new { Name = "Gold",    BackdropId = 4, Center = 0xF39C12, Edge = 0xB7770D, Pattern = 0xF1C40F, Text = 0x000000 },
            new { Name = "Violet",  BackdropId = 5, Center = 0x8E44AD, Edge = 0x6C3483, Pattern = 0x9B59B6, Text = 0xFFFFFF },
        };
        var bd = backdrops[rng.Next(backdrops.Length)];

        return
        [
            new UniqueGiftAttribute
            {
                Type = "model",
                Name = gift.Title ?? "Gift",
                RarityPermille = rng.Next(10, 500),
                DocumentId = gift.DocumentId,
                DocumentAccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                DocumentDate = gift.DocumentDate,
                MimeType = gift.MimeType,
                DocumentSize = gift.DocumentSize,
                DcId = gift.DcId,
            },
            new UniqueGiftAttribute
            {
                Type = "pattern",
                Name = gift.Title ?? "Gift",
                RarityPermille = rng.Next(10, 300),
                DocumentId = gift.DocumentId,
                DocumentAccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                DocumentDate = gift.DocumentDate,
                MimeType = gift.MimeType,
                DocumentSize = gift.DocumentSize,
                DcId = gift.DcId,
            },
            new UniqueGiftAttribute
            {
                Type = "backdrop",
                Name = bd.Name,
                RarityPermille = rng.Next(10, 300),
                BackdropId = bd.BackdropId,
                CenterColor = bd.Center,
                EdgeColor = bd.Edge,
                PatternColor = bd.Pattern,
                TextColor = bd.Text,
            },
        ];
    }
}
