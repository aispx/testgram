using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Deletes a stickerset we created.
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.deleteStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteStickerSetHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetStore stickerSetStore,
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerUpdateNotifier updateNotifier,
    ILogger<DeleteStickerSetHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestDeleteStickerSet, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestDeleteStickerSet obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveAsync(input, obj.Stickerset);
        var setId = setDocument.GetInt64("StickerSetId");
        var type = stickerSetStore.GetStickerSetType(setDocument);

        await stickerSetStore.DeleteAsync(setId);

        // Everyone who had it installed loses it, not just the creator.
        await installedStickerSetStore.RemoveForAllUsersAsync(setId);

        logger.LogInformation("Deleted sticker set {SetId} by user {UserId}", setId, input.UserId);

        await updateNotifier.NotifyStickerSetsAsync(input.UserId, type, input.AuthKeyId);

        return new TBoolTrue();
    }
}
