using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Replace a sticker in a stickerset.
/// Possible errors
/// Code Type Description
/// 400 STICKER_INVALID The provided sticker is invalid.
/// 400 STICKER_FILE_INVALID Sticker file invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.replaceSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ReplaceStickerHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestReplaceSticker,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestReplaceSticker obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveByDocumentAsync(input, obj.Sticker);
        var oldDocumentId = ((TInputDocument)obj.Sticker).Id;

        if (obj.NewSticker is not TInputStickerSetItem newSticker)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
            return null!;
        }

        await stickerSetEditor.ReplaceAsync(setDocument, oldDocumentId, newSticker);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
