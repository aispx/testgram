namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Builds a <see cref="PeerColor"/> from a unique star gift, for
/// <a href="https://core.telegram.org/api/colors">collectible peer colors</a>
/// (peerColorCollectible / inputPeerColorCollectible).
/// </summary>
public static class CollectiblePeerColorHelper
{
    public static PeerColor ToPeerColor(UniqueStarGiftDocument doc)
    {
        var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
        var backdrop = doc.Attributes.FirstOrDefault(a => a.Type == "backdrop");
        var pattern = doc.Attributes.FirstOrDefault(a => a.Type == "pattern");

        var giftEmojiId = model?.DocumentId ?? doc.DocumentId;
        var backgroundEmojiId = pattern?.DocumentId ?? giftEmojiId;

        var centerColor = backdrop?.CenterColor ?? 0;
        var edgeColor = backdrop?.EdgeColor ?? 0;

        return new PeerColor(
            Color: null,
            BackgroundEmojiId: backgroundEmojiId,
            CollectibleId: doc.UniqueId,
            GiftEmojiId: giftEmojiId,
            AccentColor: centerColor,
            Colors: [centerColor, edgeColor]);
    }
}
