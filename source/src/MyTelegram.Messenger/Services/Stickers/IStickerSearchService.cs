using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Finds sticker documents across the whole catalogue, for the suggestion popup
/// (<c>messages.getStickers</c>) and the search field (<c>messages.searchStickers</c>).
///
/// <para>Both used to scan the entire stickerset collection on every keystroke — <c>Find(Empty)</c>,
/// then a linear walk of every pack of every set. These queries go through the
/// <c>Packs.Emoticon</c> and <c>Keywords.Keyword</c> indexes instead.</para>
/// </summary>
public interface IStickerSearchService
{
    /// <summary>
    /// Documents whose pack is exactly this emoji, in catalogue order. <paramref name="emojiSets"/>
    /// selects custom emoji instead of stickers — the two are never mixed in one answer, because the
    /// methods that return them are different.
    /// </summary>
    Task<List<long>> FindByEmoticonAsync(string emoticon, bool emojiSets,
        CancellationToken cancellationToken = default);

    /// <summary>Documents whose stored keywords match, for the free-text search.</summary>
    Task<List<long>> FindByKeywordAsync(string query, bool emojiSets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The Premium examples. A Premium custom emoji is one whose
    /// <c>documentAttributeCustomEmoji.free</c> is unset; a Premium sticker is one carrying an effect,
    /// which clients recognise by a video thumbnail of type <c>"f"</c>
    /// (Android <c>MessageObject.isPremiumSticker</c>).
    /// </summary>
    Task<List<long>> FindPremiumAsync(bool emojiSets, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stickerset rows whose title or short name matches, for <c>messages.searchStickerSets</c>.
    /// </summary>
    Task<List<BsonDocument>> SearchSetsAsync(string query, StickerSetType type, bool excludeFeatured,
        int limit, CancellationToken cancellationToken = default);
}
