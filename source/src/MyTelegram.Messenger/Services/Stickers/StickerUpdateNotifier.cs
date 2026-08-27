namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerUpdateNotifier(IObjectMessageSender objectMessageSender)
    : IStickerUpdateNotifier, ITransientDependency
{
    public Task NotifyFavedAsync(long userId, long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateFavedStickers(), excludeAuthKeyId);
    }

    public Task NotifyRecentAsync(long userId, long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateRecentStickers(), excludeAuthKeyId);
    }

    public Task NotifyStickerSetsAsync(long userId, StickerSetType type, long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateStickerSets
        {
            Masks = type == StickerSetType.Mask,
            Emojis = type == StickerSetType.CustomEmoji
        }, excludeAuthKeyId);
    }

    public Task NotifyOrderAsync(long userId, StickerSetType type, IReadOnlyList<long> order,
        long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateStickerSetsOrder
        {
            Masks = type == StickerSetType.Mask,
            Emojis = type == StickerSetType.CustomEmoji,
            Order = new TVector<long>(order)
        }, excludeAuthKeyId);
    }

    public Task NotifyNewStickerSetAsync(long userId, Schema.Messages.TStickerSet stickerSet,
        long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateNewStickerSet { Stickerset = stickerSet }, excludeAuthKeyId);
    }

    public Task NotifyMoveToTopAsync(long userId, StickerSetType type, long stickerSetId,
        long? excludeAuthKeyId)
    {
        return PushAsync(userId, new TUpdateMoveStickerSetToTop
        {
            Masks = type == StickerSetType.Mask,
            Emojis = type == StickerSetType.CustomEmoji,
            Stickerset = stickerSetId
        }, excludeAuthKeyId);
    }

    public Task NotifyReadFeaturedAsync(long userId, StickerSetType type, long? excludeAuthKeyId)
    {
        IUpdate update = type == StickerSetType.CustomEmoji
            ? new TUpdateReadFeaturedEmojiStickers()
            : new TUpdateReadFeaturedStickers();

        return PushAsync(userId, update, excludeAuthKeyId);
    }

    private Task PushAsync(long userId, IUpdate update, long? excludeAuthKeyId)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        return objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
            excludeAuthKeyId: excludeAuthKeyId);
    }
}
