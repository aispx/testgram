using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Mark or unmark a sticker as favorite
/// Possible errors
/// Code Type Description
/// 400 STICKER_ID_INVALID The provided sticker ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.faveSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class FaveStickerHandler(
    IStickerDocumentListStore listStore,
    IStickerDocumentValidator documentValidator,
    IStickerLimitResolver limitResolver,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestFaveSticker, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestFaveSticker obj)
    {
        if (obj.Id is not TInputDocument inputDocument)
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
            return null!;
        }

        if (obj.Unfave)
        {
            if (await listStore.RemoveAsync(StickerDocumentListKind.Faved, input.UserId, inputDocument.Id, false))
            {
                await updateNotifier.NotifyFavedAsync(input.UserId, input.AuthKeyId);
            }

            return new TBoolTrue();
        }

        if (!await documentValidator.IsStickerAsync(inputDocument.Id))
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
        }

        // Past the limit the oldest favourite is dropped rather than the request refused: clients truncate
        // to stickers_faved_limit before hashing, so a longer list can never match again.
        await listStore.AddAsync(StickerDocumentListKind.Faved, input.UserId, inputDocument.Id, false,
            await limitResolver.GetFavedLimitAsync(input.UserId));

        await updateNotifier.NotifyFavedAsync(input.UserId, input.AuthKeyId);

        return new TBoolTrue();
    }
}
