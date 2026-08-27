using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Storage behind the installed / archived stickerset lists.
/// See https://corefork.telegram.org/api/stickers#installing-stickersets
/// </summary>
public interface IInstalledStickerSetStore
{
    /// <summary>
    /// The user's sets of one type, newest install first. Order is server-authoritative — clients
    /// render and hash the sequence they are given.
    /// </summary>
    Task<List<InstalledStickerSetDocument>> GetAsync(long userId, StickerSetType type, bool archived,
        int limit = 0, long offsetId = 0, CancellationToken cancellationToken = default);

    /// <summary>How many sets of one type the user has, used for the archived count and the install limit.</summary>
    Task<long> CountAsync(long userId, StickerSetType type, bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-user overlay for a batch of sets, so <c>stickerSet.installed_date</c> and
    /// <c>stickerSet.archived</c> can be filled in without one query per set.
    /// </summary>
    Task<Dictionary<long, InstalledStickerSetDocument>> GetOverlayAsync(long userId,
        IReadOnlyCollection<long> stickerSetIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the set, or un-archives and moves it back to the front if it was already there —
    /// re-installing an archived set is how clients un-archive it, so this must not be a no-op.
    /// Returns whether the row was newly created.
    /// </summary>
    Task<bool> InstallAsync(long userId, long stickerSetId, StickerSetType type, bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the set from the user's list; returns whether a row was actually deleted.</summary>
    Task<bool> UninstallAsync(long userId, long stickerSetId, CancellationToken cancellationToken = default);

    /// <summary>Drops the set for every user — used when its creator deletes it.</summary>
    Task RemoveForAllUsersAsync(long stickerSetId, CancellationToken cancellationToken = default);

    /// <summary>Archives or un-archives sets the user already has installed; returns the ids it touched.</summary>
    Task<List<long>> SetArchivedAsync(long userId, IReadOnlyCollection<long> stickerSetIds, bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites <c>Order</c> so the list reads in exactly the sequence given. Ids the user does not
    /// have installed are ignored, and sets missing from the vector keep their relative position
    /// below the ones that were listed.
    /// </summary>
    Task ReorderAsync(long userId, StickerSetType type, IReadOnlyList<long> orderedStickerSetIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one set to the front, for the <c>update_stickersets_order</c> flag on the send methods.
    /// Returns false when the user does not have the set installed, so the caller can skip the update.
    /// </summary>
    Task<bool> MoveToTopAsync(long userId, long stickerSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the oldest non-archived sets past <paramref name="limit"/> and returns their ids —
    /// how the official server answers <c>messages.stickerSetInstallResultArchive</c>.
    /// </summary>
    Task<List<long>> ArchiveOverflowAsync(long userId, StickerSetType type, int limit,
        CancellationToken cancellationToken = default);
}
