using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class UpgradeAttributeHelper
{
    public static async Task<TVector<IStarGiftAttribute>> GetAllAsync(IMongoDatabase db, StarGiftDocument gift)
    {
        var col = db.GetCollection<UpgradeConfigEntry>("star-gift-upgrade-config");
        var all = await col.Find(Builders<UpgradeConfigEntry>.Filter.In("gift_id", new[] { gift.GiftId, 0L })).ToListAsync();

        var models    = all.Where(e => e.Type == "model"   && e.GiftId == gift.GiftId).ToList();
        if (models.Count == 0) models = all.Where(e => e.Type == "model" && e.GiftId == 0).ToList();
        var patterns  = all.Where(e => e.Type == "pattern" && e.GiftId == gift.GiftId).ToList();
        if (patterns.Count == 0) patterns = all.Where(e => e.Type == "pattern" && e.GiftId == 0).ToList();
        var backdrops = all.Where(e => e.Type == "backdrop").ToList();

        var attrs = new TVector<IStarGiftAttribute>();

        // Pick ONE random model
        if (models.Count > 0)
        {
            var e = WeightedRandom(models);
            attrs.Add(new TStarGiftAttributeModel
            {
                Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille,
            });
        }
        else
        {
            // Fallback to gift sticker
            attrs.Add(new TStarGiftAttributeModel
            {
                Name = gift.Title ?? "Gift",
                Document = new TDocument
                {
                    Id = gift.DocumentId, AccessHash = gift.DocumentAccessHash,
                    FileReference = gift.FileReference ?? [], Date = gift.DocumentDate,
                    MimeType = gift.MimeType ?? "application/x-tgsticker",
                    Size = gift.DocumentSize, DcId = gift.DcId,
                    Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
                },
                RarityPermille = 100,
            });
        }

        // Pick ONE random pattern
        if (patterns.Count > 0)
        {
            var e = WeightedRandom(patterns);
            attrs.Add(new TStarGiftAttributePattern
            {
                Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille,
            });
        }
        else
        {
            // Fallback to gift sticker
            attrs.Add(new TStarGiftAttributePattern
            {
                Name = gift.Title ?? "Gift",
                Document = new TDocument
                {
                    Id = gift.DocumentId, AccessHash = gift.DocumentAccessHash,
                    FileReference = gift.FileReference ?? [], Date = gift.DocumentDate,
                    MimeType = gift.MimeType ?? "application/x-tgsticker",
                    Size = gift.DocumentSize, DcId = gift.DcId,
                    Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
                },
                RarityPermille = 100,
            });
        }

        // Pick ONE random backdrop
        if (backdrops.Count > 0)
        {
            var e = WeightedRandom(backdrops);
            attrs.Add(new TStarGiftAttributeBackdrop
            {
                Name = e.Name, BackdropId = e.BackdropId ?? 0,
                CenterColor = e.CenterColor ?? 0, EdgeColor = e.EdgeColor ?? 0,
                PatternColor = e.PatternColor ?? 0, TextColor = e.TextColor ?? 0,
                RarityPermille = e.RarityPermille,
            });
        }
        else
        {
            // Fallback to default backdrop
            attrs.Add(new TStarGiftAttributeBackdrop
            {
                Name = "Default", BackdropId = 1,
                CenterColor = 0x2980B9, EdgeColor = 0x1A5276,
                PatternColor = 0x3498DB, TextColor = 0xFFFFFF,
                RarityPermille = 100,
            });
        }

        return attrs;
    }

    private static T WeightedRandom<T>(List<T> items) where T : UpgradeConfigEntry
    {
        var total = items.Sum(e => e.RarityPermille);
        var r = Random.Shared.Next(total);
        var acc = 0;
        foreach (var e in items) { acc += e.RarityPermille; if (r < acc) return e; }
        return items[^1];
    }

    private static IDocument MakeDoc(UpgradeConfigEntry e) => new TDocument
    {
        Id = e.DocumentId ?? 0, AccessHash = e.DocumentAccessHash ?? 0,
        FileReference = e.FileReference ?? [], Date = e.DocumentDate ?? 0,
        MimeType = e.MimeType ?? "application/x-tgsticker",
        Size = e.DocumentSize ?? 0, DcId = e.DcId ?? 2,
        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
    };
}
