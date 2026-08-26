using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get all archived stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getArchivedStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetArchivedStickersHandler(
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerSetStore stickerSetStore,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetArchivedStickers,
        MyTelegram.Schema.Messages.IArchivedStickers>
{
    protected override async Task<IArchivedStickers> HandleCoreAsync(IRequestInput input,
        RequestGetArchivedStickers obj)
    {
        var type = obj.Emojis
            ? StickerSetType.CustomEmoji
            : obj.Masks
                ? StickerSetType.Mask
                : StickerSetType.Regular;

        // count is the whole point of the method for most callers: Android asks with limit = 0 purely to
        // put a number next to the "Archived stickers" row (MediaDataController.loadArchivedStickersCount),
        // and hides the row entirely when it is zero.
        var count = await installedStickerSetStore.CountAsync(input.UserId, type, true);
        if (obj.Limit <= 0 || count == 0)
        {
            return new TArchivedStickers
            {
                Count = (int)count,
                Sets = new TVector<IStickerSetCovered>()
            };
        }

        var rows = await installedStickerSetStore.GetAsync(input.UserId, type, true, obj.Limit, obj.OffsetId);
        var catalogue = await stickerSetStore.FindManyAsync(rows.ConvertAll(p => p.StickerSetId));
        var setDocuments = rows
            .Where(p => catalogue.ContainsKey(p.StickerSetId))
            .Select(p => catalogue[p.StickerSetId])
            .ToList();

        return new TArchivedStickers
        {
            Count = (int)count,
            Sets = new TVector<IStickerSetCovered>(
                await stickerSetMapper.BuildCoveredAsync(input, setDocuments, false))
        };
    }
}
