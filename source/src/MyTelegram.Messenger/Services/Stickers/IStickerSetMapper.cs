using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Turns catalogue rows into the TL objects the sticker methods return.
///
/// <para>Every one of the sticker handlers used to build these by hand, and they disagreed: some left
/// <c>Thumbs</c> empty so previews never loaded, some fabricated
/// <c>documentAttributeSticker.stickerset = inputStickerSetEmpty</c> so long-pressing a sticker could
/// not open its pack, and all of them sent <c>stickerSet.hash = 0</c>, which is the client's "nothing
/// cached" sentinel.</para>
/// </summary>
public interface IStickerSetMapper
{
    /// <summary>
    /// The <c>stickerSet</c> header, with the per-user flags (<c>installed_date</c>, <c>archived</c>)
    /// taken from <paramref name="installed"/> — pass the row from
    /// <see cref="IInstalledStickerSetStore.GetOverlayAsync"/>, or null for a set the user does not have.
    /// </summary>
    Schema.TStickerSet BuildHeader(IRequestInput input, BsonDocument stickerSetDocument,
        InstalledStickerSetDocument? installed);

    /// <summary>
    /// The full <c>messages.stickerSet</c>: header, packs, keywords and every document. This is what
    /// <c>messages.getStickerSet</c> and all of <c>stickers.*</c> must return — clients treat the
    /// response as the new state of the set, so an answer with empty <c>documents</c> empties the pack
    /// in the client (Android <c>MediaDataController.putStickerSet</c>).
    /// </summary>
    Task<Schema.Messages.TStickerSet> BuildFullAsync(IRequestInput input, BsonDocument stickerSetDocument,
        string? diceEmoticon = null, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="BuildFullAsync"/>, resolving the set by id first.</summary>
    Task<Schema.Messages.TStickerSet?> BuildFullByIdAsync(IRequestInput input, long stickerSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stickerset previews. <paramref name="full"/> selects <c>stickerSetFullCovered</c>, which carries
    /// every document and spares the client a follow-up <c>getStickerSet</c>; the official server only
    /// uses it for custom emoji sets.
    /// </summary>
    Task<List<IStickerSetCovered>> BuildCoveredAsync(IRequestInput input,
        IReadOnlyList<BsonDocument> stickerSetDocuments, bool full,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Documents by id, in the order given, for the flat lists (recent, faved, suggestions, search).
    /// Ids with no document row are dropped — see the note on the return value of
    /// <see cref="BuildDocumentsAsync"/>.
    /// </summary>
    Task<List<IDocument>> BuildDocumentsAsync(IRequestInput input, IReadOnlyList<long> documentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The <c>stickerPack</c> vector covering the given documents, grouped by emoji. The flat lists
    /// carry one too, and clients build their emoji-to-sticker index from it; returning it empty is why
    /// typing an emoji suggested nothing from the favourites.
    /// </summary>
    Task<List<IStickerPack>> BuildPacksForDocumentsAsync(IReadOnlyList<long> documentIds,
        CancellationToken cancellationToken = default);
}
