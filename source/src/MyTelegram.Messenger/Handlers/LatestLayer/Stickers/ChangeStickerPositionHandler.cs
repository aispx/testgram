using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Changes the absolute position of a sticker in the set it belongs to.
/// Possible errors
/// Code Type Description
/// 400 STICKER_INVALID The provided sticker is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.changeStickerPosition"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ChangeStickerPositionHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestChangeStickerPosition,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestChangeStickerPosition obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveByDocumentAsync(input, obj.Sticker);
        var documentId = ((TInputDocument)obj.Sticker).Id;

        await stickerSetEditor.MoveAsync(setDocument, documentId, obj.Position);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
