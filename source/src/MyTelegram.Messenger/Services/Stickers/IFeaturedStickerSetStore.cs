using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// The trending stickerset lists and their per-user read state.
/// See https://corefork.telegram.org/api/stickers#featured-stickersets
/// </summary>
public interface IFeaturedStickerSetStore
{
    /// <summary>
    /// The catalogue rows of the trending sets, in display order. Falls back to the sets flagged
    /// <c>Official</c> when nothing has been seeded, so a fresh deployment shows a plausible trending
    /// page instead of an empty one.
    /// </summary>
    Task<List<BsonDocument>> GetFeaturedAsync(StickerSetType type, int offset = 0, int limit = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The sets that have dropped out of the current trending list, for
    /// <c>messages.getOldFeaturedStickers</c>. Never falls back — an empty history is a real answer.
    /// </summary>
    Task<List<BsonDocument>> GetOldFeaturedAsync(StickerSetType type, int offset, int limit,
        CancellationToken cancellationToken = default);

    Task<int> CountOldFeaturedAsync(StickerSetType type, CancellationToken cancellationToken = default);

    /// <summary>The trending sets this user has already been shown.</summary>
    Task<HashSet<long>> GetReadIdsAsync(long userId, StickerSetType type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks sets as read. Returns false when nothing changed, so the caller can skip the update it would
    /// otherwise push to the user's other sessions.
    /// </summary>
    Task<bool> MarkReadAsync(long userId, StickerSetType type, IReadOnlyCollection<long> stickerSetIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>messages.featuredStickers.hash</c>.
///
/// <para>Reproduces the client's own computation, which is not a free choice: Android
/// <c>MediaDataController.calcFeaturedStickersHash</c> and tdlib
/// <c>StickersManager::get_featured_sticker_sets_hash</c> both fold in each set id and then an extra
/// <c>1</c> for every set still unread. Answering with anything else — the collection's <c>Version</c>,
/// for instance — means <c>featuredStickersNotModified</c> can never match and the whole trending list is
/// re-downloaded on every poll.</para>
/// </summary>
public static class FeaturedStickerSetHashHelper
{
    public static long ComputeHash(IEnumerable<long> stickerSetIds, ISet<long> unreadStickerSetIds)
    {
        var acc = 0UL;

        foreach (var stickerSetId in stickerSetIds)
        {
            acc = VectorHashHelper.Mix(acc, stickerSetId);

            if (unreadStickerSetIds.Contains(stickerSetId))
            {
                acc = VectorHashHelper.Mix(acc, 1);
            }
        }

        return (long)acc;
    }
}
