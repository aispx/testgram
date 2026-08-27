namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Tells the user's other sessions that one of their sticker lists changed.
///
/// <para>Each of these updates is a pure invalidation carrying no data — the session that made the change
/// already has the RPC result, so it is excluded, and every other session answers by re-fetching the list
/// it names. Without them a second device keeps showing the old favourites, the old order and the old
/// installed sets until its own hourly refresh happens to run.</para>
/// See https://corefork.telegram.org/api/stickers
/// </summary>
public interface IStickerUpdateNotifier
{
    /// <summary>Should trigger <c>messages.getFavedStickers</c>.</summary>
    Task NotifyFavedAsync(long userId, long? excludeAuthKeyId);

    /// <summary>Should trigger <c>messages.getRecentStickers</c>.</summary>
    Task NotifyRecentAsync(long userId, long? excludeAuthKeyId);

    /// <summary>
    /// Should trigger <c>messages.getAllStickers</c> / <c>getMaskStickers</c> / <c>getEmojiStickers</c> and
    /// <c>messages.getArchivedStickers</c>, for install, uninstall, archive and unarchive alike.
    /// </summary>
    Task NotifyStickerSetsAsync(long userId, StickerSetType type, long? excludeAuthKeyId);

    /// <summary>
    /// Carries the new order, so a receiving session can apply it without re-fetching — it still should,
    /// per the API docs, but the vector is part of the constructor.
    /// </summary>
    Task NotifyOrderAsync(long userId, StickerSetType type, IReadOnlyList<long> order, long? excludeAuthKeyId);

    /// <summary>
    /// A set was just installed. Carries the whole set, which is why installing on one device makes it
    /// appear on the others without a round trip.
    /// </summary>
    Task NotifyNewStickerSetAsync(long userId, Schema.Messages.TStickerSet stickerSet, long? excludeAuthKeyId);

    /// <summary>
    /// A set moved to the top of the panel because the user sent one of its stickers — the effect of the
    /// <c>update_stickersets_order</c> flag on the send methods.
    /// </summary>
    Task NotifyMoveToTopAsync(long userId, StickerSetType type, long stickerSetId, long? excludeAuthKeyId);

    /// <summary>The trending badge was cleared.</summary>
    Task NotifyReadFeaturedAsync(long userId, StickerSetType type, long? excludeAuthKeyId);
}
