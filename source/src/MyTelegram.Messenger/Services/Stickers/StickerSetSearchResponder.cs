using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// The shared body of <c>messages.searchStickerSets</c> and <c>messages.searchEmojiStickerSets</c>, which
/// differ only in which kind of set they look through.
/// </summary>
public static class StickerSetSearchResponder
{
    private const int Limit = 20;

    public static async Task<IFoundStickerSets> RespondAsync(IRequestInput input,
        IStickerSearchService stickerSearchService, IStickerSetMapper stickerSetMapper,
        string? query, StickerSetType type, bool excludeFeatured, long requestHash,
        CancellationToken cancellationToken = default)
    {
        var setDocuments = await stickerSearchService.SearchSetsAsync(query ?? string.Empty, type,
            excludeFeatured, Limit, cancellationToken);

        // Over the ids of the results, in order: the client caches the answer per query string and quotes
        // the hash back on the next keystroke that produces the same string.
        var hash = VectorHashHelper.ComputeHash(setDocuments.Select(p => p.GetInt64("StickerSetId")));

        if (requestHash != 0 && requestHash == hash)
        {
            return new TFoundStickerSetsNotModified();
        }

        // Custom emoji sets go out in full, as they do everywhere else: a client showing an emoji set needs
        // every document at once, and the covers alone would force a getStickerSet per result.
        var full = type == StickerSetType.CustomEmoji;

        return new TFoundStickerSets
        {
            Hash = hash,
            Sets = new TVector<IStickerSetCovered>(
                await stickerSetMapper.BuildCoveredAsync(input, setDocuments, full, cancellationToken))
        };
    }
}
