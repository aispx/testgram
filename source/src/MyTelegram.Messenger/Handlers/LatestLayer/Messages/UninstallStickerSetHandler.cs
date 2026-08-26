using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Uninstall a stickerset
/// Possible errors
/// Code Type Description
/// 406 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.uninstallStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UninstallStickerSetHandler(
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUninstallStickerSet, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestUninstallStickerSet obj)
    {
        var lookup = await stickerSetStore.FindAsync(obj.Stickerset);
        if (lookup.Set == null)
        {
            // 406 rather than 400 here: the method is documented that way, and clients treat a 406 as
            // "drop this set from the panel", which is exactly right for a set that no longer exists.
            RpcErrors.RpcErrors406.StickersetInvalid.ThrowRpcError();
        }

        var setDocument = lookup.Set!;
        var setId = setDocument.GetInt64("StickerSetId");

        if (!await installedStickerSetStore.UninstallAsync(input.UserId, setId))
        {
            // Not installed: nothing to do, and the official server still answers true. Skipping the
            // update keeps a stray uninstall from making every other session refetch for nothing.
            return new TBoolTrue();
        }

        await updateNotifier.NotifyStickerSetsAsync(input.UserId, stickerSetStore.GetStickerSetType(setDocument),
            input.AuthKeyId);

        return new TBoolTrue();
    }
}
