using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Set stickerset thumbnail
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.setStickerSetThumb"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetStickerSetThumbHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestSetStickerSetThumb,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestSetStickerSetThumb obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveAsync(input, obj.Stickerset);

        // Either form names a document already in the set; thumb_document_id is what custom emoji sets use,
        // where the thumbnail is one of the emoji rather than a separately uploaded image.
        var thumbDocumentId = obj.Thumb is TInputDocument inputDocument
            ? inputDocument.Id
            : obj.ThumbDocumentId;

        await stickerSetEditor.SetThumbAsync(setDocument, thumbDocumentId);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
