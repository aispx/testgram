using MyTelegram.Schema.Messages;
using IStickerSet = MyTelegram.Schema.IStickerSet;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Serves the three installed-stickerset lists — <c>messages.getAllStickers</c>,
/// <c>getMaskStickers</c> and <c>getEmojiStickers</c> — which differ only in which kind of set they
/// cover.
/// </summary>
public interface IInstalledStickerSetListService
{
    Task<IAllStickers> GetAsync(IRequestInput input, StickerSetType type, long requestHash,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class InstalledStickerSetListService(
    IInstalledStickerSetStore installedStickerSetStore,
    IStickerSetStore stickerSetStore,
    IStickerSetMapper stickerSetMapper) : IInstalledStickerSetListService, ITransientDependency
{
    public async Task<IAllStickers> GetAsync(IRequestInput input, StickerSetType type, long requestHash,
        CancellationToken cancellationToken = default)
    {
        var installed = await installedStickerSetStore.GetAsync(input.UserId, type, false,
            cancellationToken: cancellationToken);

        // One query for the catalogue instead of one per set; the order stays the store's.
        var catalogue = await stickerSetStore.FindManyAsync(installed.ConvertAll(p => p.StickerSetId),
            cancellationToken);

        var sets = new List<IStickerSet>(installed.Count);
        var setHashes = new List<long>(installed.Count);

        foreach (var row in installed)
        {
            if (!catalogue.TryGetValue(row.StickerSetId, out var setDocument))
            {
                // The set was deleted out from under the user. Skipping it here rather than answering with
                // a set the client cannot load also keeps the hash honest.
                continue;
            }

            var header = stickerSetMapper.BuildHeader(input, setDocument, row);
            sets.Add(header);
            setHashes.Add(header.Hash);
        }

        // Android MediaDataController.calcStickersHash and tdlib
        // StickersManager::get_sticker_sets_hash both fold in each set's own hash, not its id — so the
        // per-set hash has to be a real value for this to ever match. It used to be sent as 0.
        var hash = VectorHashHelper.ComputeHash(setHashes);

        if (requestHash != 0 && requestHash == hash)
        {
            return new TAllStickersNotModified();
        }

        return new TAllStickers
        {
            Hash = hash,
            Sets = new TVector<IStickerSet>(sets)
        };
    }
}
