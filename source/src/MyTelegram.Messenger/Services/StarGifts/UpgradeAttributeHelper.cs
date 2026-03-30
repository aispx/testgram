using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class UpgradeAttributeHelper
{
    public static async Task<TVector<IStarGiftAttribute>> GetAllAsync(IMongoDatabase db, StarGiftDocument gift)
    {
        var all = await db.GetCollection<UpgradeConfigEntry>("star-gift-upgrade-config")
            .Find(FilterDefinition<UpgradeConfigEntry>.Empty)
            .ToListAsync();

        var attrs = new TVector<IStarGiftAttribute>();
        foreach (var e in all)
        {
            IStarGiftAttribute attr = e.Type switch
            {
                "backdrop" => new TStarGiftAttributeBackdrop
                {
                    Name = e.Name, BackdropId = e.BackdropId ?? 0,
                    CenterColor = e.CenterColor ?? 0, EdgeColor = e.EdgeColor ?? 0,
                    PatternColor = e.PatternColor ?? 0, TextColor = e.TextColor ?? 0,
                    RarityPermille = e.RarityPermille,
                },
                "pattern" => new TStarGiftAttributePattern
                {
                    Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille,
                },
                _ => new TStarGiftAttributeModel
                {
                    Name = e.Name, Document = MakeDoc(e), RarityPermille = e.RarityPermille,
                },
            };
            attrs.Add(attr);
        }
        return attrs;
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
