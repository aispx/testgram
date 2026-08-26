using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;

/// <summary>
/// Renames a stickerset.
/// Possible errors
/// Code Type Description
/// 400 STICKERSET_INVALID The provided sticker set is invalid.
/// 400 PACK_TITLE_INVALID The stickerpack name is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.renameStickerSet"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class RenameStickerSetHandler(
    IOwnedStickerSetResolver ownedStickerSetResolver,
    IStickerSetStore stickerSetStore,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestRenameStickerSet,
        MyTelegram.Schema.Messages.IStickerSet>
{
    /// <summary>
    /// Telegram's own ceiling for a pack title, from the stickerset creation flow in @Stickers.
    /// </summary>
    private const int TitleMaxLength = 64;

    protected override async Task<MyTelegram.Schema.Messages.IStickerSet> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Stickers.RequestRenameStickerSet obj)
    {
        var title = obj.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > TitleMaxLength)
        {
            RpcErrors.RpcErrors400.PackTitleInvalid.ThrowRpcError();
        }

        var setDocument = await ownedStickerSetResolver.ResolveAsync(input, obj.Stickerset);

        setDocument["Title"] = title;
        // The revision is what makes the new title reach clients: the set's contents did not change, so
        // without it the hash stays the same and every client keeps showing the old name.
        setDocument["Version"] = setDocument.GetInt64("Version") + 1;

        await stickerSetStore.ReplaceAsync(setDocument);

        return await stickerSetMapper.BuildFullAsync(input, setDocument);
    }
}
