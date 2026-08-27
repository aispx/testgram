using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Serves the trending stickerset lists — <c>messages.getFeaturedStickers</c>,
/// <c>getFeaturedEmojiStickers</c> and <c>getOldFeaturedStickers</c>.
/// See https://corefork.telegram.org/api/stickers#featured-stickersets
/// </summary>
public interface IFeaturedStickerSetListService
{
    Task<IFeaturedStickers> GetFeaturedAsync(IRequestInput input, StickerSetType type, long requestHash,
        CancellationToken cancellationToken = default);

    Task<IFeaturedStickers> GetOldFeaturedAsync(IRequestInput input, StickerSetType type, int offset, int limit,
        long requestHash, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class FeaturedStickerSetListService(
    IFeaturedStickerSetStore featuredStickerSetStore,
    IStickerSetMapper stickerSetMapper) : IFeaturedStickerSetListService, ITransientDependency
{
    public async Task<IFeaturedStickers> GetFeaturedAsync(IRequestInput input, StickerSetType type,
        long requestHash, CancellationToken cancellationToken = default)
    {
        var setDocuments = await featuredStickerSetStore.GetFeaturedAsync(type,
            cancellationToken: cancellationToken);
        var read = await featuredStickerSetStore.GetReadIdsAsync(input.UserId, type, cancellationToken);

        var setIds = setDocuments.ConvertAll(p => p.GetInt64("StickerSetId"));
        var unread = setIds.Where(p => !read.Contains(p)).ToList();
        var hash = FeaturedStickerSetHashHelper.ComputeHash(setIds, unread.ToHashSet());

        if (requestHash != 0 && requestHash == hash)
        {
            return new TFeaturedStickersNotModified { Count = setIds.Count };
        }

        // stickerSetFullCovered carries every document and spares the client a getStickerSet per set; the
        // official server only sends it for custom emoji, where a set is small and needed all at once.
        var full = type == StickerSetType.CustomEmoji;

        return new TFeaturedStickers
        {
            Hash = hash,
            Count = setIds.Count,
            Sets = new TVector<IStickerSetCovered>(
                await stickerSetMapper.BuildCoveredAsync(input, setDocuments, full, cancellationToken)),
            Unread = new TVector<long>(unread)
        };
    }

    public async Task<IFeaturedStickers> GetOldFeaturedAsync(IRequestInput input, StickerSetType type,
        int offset, int limit, long requestHash, CancellationToken cancellationToken = default)
    {
        var total = await featuredStickerSetStore.CountOldFeaturedAsync(type, cancellationToken);
        var setDocuments = await featuredStickerSetStore.GetOldFeaturedAsync(type, offset,
            limit > 0 ? limit : 20, cancellationToken);

        var setIds = setDocuments.ConvertAll(p => p.GetInt64("StickerSetId"));

        // Sets that already left the trending list are never unread, so the hash is the plain vector hash
        // over the page's ids — the extra 1-per-unread term of the current list cannot apply here.
        var hash = FeaturedStickerSetHashHelper.ComputeHash(setIds, new HashSet<long>());

        if (requestHash != 0 && requestHash == hash)
        {
            return new TFeaturedStickersNotModified { Count = total };
        }

        return new TFeaturedStickers
        {
            Hash = hash,
            Count = total,
            Sets = new TVector<IStickerSetCovered>(
                await stickerSetMapper.BuildCoveredAsync(input, setDocuments, false, cancellationToken)),
            Unread = new TVector<long>()
        };
    }
}
