using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Reorder installed stickersets
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.reorderStickerSets"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReorderStickerSetsHandler(
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReorderStickerSets, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestReorderStickerSets obj)
    {
        if (obj.Order == null || obj.Order.Count == 0)
        {
            return new TBoolTrue();
        }

        // Each of the three panels is ordered independently, and the client sends the whole panel it just
        // rearranged, top first.
        var type = obj.Emojis
            ? StickerSetType.CustomEmoji
            : obj.Masks
                ? StickerSetType.Mask
                : StickerSetType.Regular;

        List<long> order = [..obj.Order];

        await installedStickerSetStore.ReorderAsync(input.UserId, type, order);
        await updateNotifier.NotifyOrderAsync(input.UserId, type, order, input.AuthKeyId);

        return new TBoolTrue();
    }
}
