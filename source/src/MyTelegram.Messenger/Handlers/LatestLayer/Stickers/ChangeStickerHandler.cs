using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Update the emoji, mask coordinates or keywords of a sticker in a set we created.
/// Possible errors
/// Code Type Description
/// 400 STICKER_INVALID The provided sticker is invalid.
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.changeSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ChangeStickerHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestChangeSticker,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestChangeSticker obj)
    {
        // The set is identified by the sticker, not named in the request.
        var setDocument = await ownedStickerSetResolver.ResolveByDocumentAsync(input, obj.Sticker);
        var documentId = ((TInputDocument)obj.Sticker).Id;

        await stickerSetEditor.ChangeAsync(setDocument, documentId, obj.Emoji, obj.MaskCoords, obj.Keywords);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
