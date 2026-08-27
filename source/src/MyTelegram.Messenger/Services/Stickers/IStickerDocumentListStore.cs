namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>Which per-user flat list of stickers is meant.</summary>
public enum StickerDocumentListKind
{
    /// <summary><c>messages.getFavedStickers</c>.</summary>
    Faved,

    /// <summary><c>messages.getRecentStickers</c>.</summary>
    Recent
}

/// <param name="DocumentId">The sticker.</param>
/// <param name="Date">
/// When it entered the list. <c>messages.recentStickers</c> returns these alongside the documents, in the
/// same order.
/// </param>
public readonly record struct StickerDocumentListEntry(long DocumentId, int Date);

/// <summary>
/// The favourites and recents lists.
///
/// <para>Both are ordered newest-first and both are capped by the server, not the client: past the limit
/// the oldest entry is deleted. That is not a nicety — clients truncate a list to the limit
/// <b>before</b> hashing it, so returning more than the limit permanently breaks
/// <c>favedStickersNotModified</c> / <c>recentStickersNotModified</c>.</para>
/// See https://corefork.telegram.org/api/stickers#favorite-stickersets
/// </summary>
public interface IStickerDocumentListStore
{
    /// <summary>Newest first, at most <paramref name="limit"/> entries.</summary>
    Task<List<StickerDocumentListEntry>> GetAsync(StickerDocumentListKind kind, long userId, bool attached,
        int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the sticker at the front — re-adding one that is already there moves it, which is what both
    /// clients and the hash expect — then evicts anything past <paramref name="limit"/>.
    /// </summary>
    Task AddAsync(StickerDocumentListKind kind, long userId, long documentId, bool attached, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether a row was actually deleted.</summary>
    Task<bool> RemoveAsync(StickerDocumentListKind kind, long userId, long documentId, bool attached,
        CancellationToken cancellationToken = default);

    /// <summary>Empties the list; returns whether anything was there.</summary>
    Task<bool> ClearAsync(StickerDocumentListKind kind, long userId, bool attached,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops entries whose document no longer exists, so what we store cannot disagree with what we
    /// return: a document we hand out but the client discards leaves its list shorter than ours and the
    /// hash mismatched forever.
    /// </summary>
    Task RemoveManyAsync(StickerDocumentListKind kind, long userId, IReadOnlyCollection<long> documentIds,
        bool attached, CancellationToken cancellationToken = default);
}
