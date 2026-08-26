using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Applies the <c>stickers.*</c> edits to a set the caller owns.
///
/// <para>All of it goes through one place because the catalogue row has four fields that must stay in
/// agreement — <c>DocumentIds</c>, <c>Packs</c>, <c>Keywords</c> and <c>Count</c> — plus a <c>Version</c>
/// that has to move on every edit, or the set's hash does not change and clients keep the copy they have.
/// The handlers each maintained their own subset of that and drifted: packs were appended one per
/// sticker instead of grouped by emoji, mask coordinates and keywords were dropped on the floor, and the
/// count was recomputed in some paths but not others.</para>
/// See https://corefork.telegram.org/api/stickers#creating-stickersets
/// </summary>
public interface IStickerSetEditor
{
    /// <summary>
    /// Creates the catalogue row. Throws the appropriate RPC error for a duplicate short name, an empty
    /// sticker list or a document that is not a usable sticker.
    /// </summary>
    Task<BsonDocument> CreateAsync(long ownerUserId, long stickerSetId, string title, string shortName,
        bool masks, bool emojis, bool textColor, IReadOnlyList<TInputStickerSetItem> stickers,
        long? thumbDocumentId, CancellationToken cancellationToken = default);

    /// <summary>Appends a sticker, or moves it if the set already contains it.</summary>
    Task AddAsync(BsonDocument stickerSetDocument, TInputStickerSetItem sticker,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(BsonDocument stickerSetDocument, long documentId,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a sticker to <paramref name="position"/>, clamped to the set's bounds.</summary>
    Task MoveAsync(BsonDocument stickerSetDocument, long documentId, int position,
        CancellationToken cancellationToken = default);

    /// <summary>Swaps one sticker for another, keeping the position, emoji and keywords of the old one.</summary>
    Task ReplaceAsync(BsonDocument stickerSetDocument, long oldDocumentId, TInputStickerSetItem newSticker,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits the emoji, mask coordinates or keywords of a sticker already in the set. A null argument
    /// leaves that property alone, which is what the flags of <c>stickers.changeSticker</c> mean.
    /// </summary>
    Task ChangeAsync(BsonDocument stickerSetDocument, long documentId, string? emoji, IMaskCoords? maskCoords,
        string? keywords, CancellationToken cancellationToken = default);

    /// <summary>
    /// Points the set at a thumbnail document, or clears it. Writes <c>ThumbDocumentId</c>, the field the
    /// read path serves as <c>stickerSet.thumb_document_id</c>.
    /// </summary>
    Task SetThumbAsync(BsonDocument stickerSetDocument, long? thumbDocumentId,
        CancellationToken cancellationToken = default);
}
