using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Remove a sticker from the set where it belongs, bots only. The sticker set must have been created by the bot.
/// Possible errors
/// Code Type Description
/// 400 STICKER_INVALID The provided sticker is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.removeStickerFromSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class RemoveStickerFromSetHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestRemoveStickerFromSet,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestRemoveStickerFromSet obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveByDocumentAsync(input, obj.Sticker);
        var documentId = ((TInputDocument)obj.Sticker).Id;

        await stickerSetEditor.RemoveAsync(setDocument, documentId);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
