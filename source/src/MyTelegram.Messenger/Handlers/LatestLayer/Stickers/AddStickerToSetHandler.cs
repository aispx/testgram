using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Add a sticker to a stickerset, bots only. The sticker set must have been created by the bot.
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// 400 STICKER_FILE_INVALID Sticker file invalid.
/// 400 STICKERPACK_STICKERS_TOO_MUCH There are too many stickers in this stickerpack, you can't add any more.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.addStickerToSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class AddStickerToSetHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetEditor stickerSetEditor,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestAddStickerToSet,
        MyTelegram.Schema.Messages.IStickerSet>
{
    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestAddStickerToSet obj)
    {
        var setDocument = await ownedStickerSetResolver.ResolveAsync(input, obj.Stickerset);

        if (obj.Sticker is not TInputStickerSetItem sticker)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
            return null!;
        }

        await stickerSetEditor.AddAsync(setDocument, sticker);

        // The full set, not just its header: clients replace their cached copy with whatever this returns.
        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
