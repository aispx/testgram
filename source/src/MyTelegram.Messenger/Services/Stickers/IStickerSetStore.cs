using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// The global stickerset catalogue: resolving an <c>InputStickerSet</c> to a row, and looking a set up
/// by one of its documents.
///
/// <para>Reads <c>eventflow-stickersetreadmodel</c>, which the seeder scripts fill and
/// <c>stickers.createStickerSet</c> appends to. The dice and special-set name tables used to be
/// duplicated in <c>GetStickerSetHandler</c> and <c>InstallStickerSetHandler</c>, and had already
/// drifted apart — <c>inputStickerSetTonGifts</c> resolved in one and not the other.</para>
/// </summary>
public interface IStickerSetStore
{
    /// <summary>
    /// Resolves any <c>InputStickerSet</c> constructor. <c>Emoticon</c> is set only for
    /// <c>inputStickerSetDice</c>, where it is also the pack the documents belong to.
    /// </summary>
    Task<StickerSetLookup> FindAsync(IInputStickerSet? inputStickerSet,
        CancellationToken cancellationToken = default);

    Task<BsonDocument?> FindByIdAsync(long stickerSetId, CancellationToken cancellationToken = default);

    Task<BsonDocument?> FindByShortNameAsync(string shortName, CancellationToken cancellationToken = default);

    /// <summary>
    /// The set that contains the given document. This is what the per-sticker methods
    /// (<c>changeSticker</c>, <c>changeStickerPosition</c>, <c>removeStickerFromSet</c>,
    /// <c>replaceSticker</c>) need: they receive only an <c>InputDocument</c>.
    /// </summary>
    Task<BsonDocument?> FindByDocumentIdAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>Catalogue rows for many sets in one query, keyed by <c>StickerSetId</c>.</summary>
    Task<Dictionary<long, BsonDocument>> FindManyAsync(IReadOnlyCollection<long> stickerSetIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets flagged <c>Official</c>, ordered by size. Used as the fallback trending list when no
    /// featured sets have been seeded.
    /// </summary>
    Task<List<BsonDocument>> FindOfficialAsync(StickerSetType type, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the short name is free, for <c>stickers.checkShortName</c>.</summary>
    Task<bool> ShortNameExistsAsync(string shortName, CancellationToken cancellationToken = default);

    /// <summary>
    /// The sets this user created, newest first, for <c>messages.getMyStickers</c>. Paginated by
    /// <c>offset_id</c>, which is the id of the last set already seen.
    /// </summary>
    Task<List<BsonDocument>> FindByCreatorAsync(long creatorUserId, long offsetId, int limit,
        CancellationToken cancellationToken = default);

    Task<int> CountByCreatorAsync(long creatorUserId, CancellationToken cancellationToken = default);

    /// <summary>Writes a modified catalogue row back.</summary>
    Task ReplaceAsync(BsonDocument stickerSetDocument, CancellationToken cancellationToken = default);

    Task InsertAsync(BsonDocument stickerSetDocument, CancellationToken cancellationToken = default);

    Task DeleteAsync(long stickerSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which list the set belongs to. Derived from the <c>Masks</c> / <c>Emojis</c> flags rather than a
    /// stored enum, because the seeded rows only carry the flags.
    /// </summary>
    StickerSetType GetStickerSetType(BsonDocument stickerSetDocument);
}

/// <param name="Set">The catalogue row, or null when nothing matched.</param>
/// <param name="Emoticon">
/// The dice emoji for <c>inputStickerSetDice</c>. Such a set has no packs of its own, so the emoticon
/// is what the response uses to build the single pack covering every document.
/// </param>
public readonly record struct StickerSetLookup(BsonDocument? Set, string? Emoticon = null);
