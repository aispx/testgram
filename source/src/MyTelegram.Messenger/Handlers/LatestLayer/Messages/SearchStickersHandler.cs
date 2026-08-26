using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Search for stickers using keywords and/or emoji.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchStickersHandler(
    IStickerSearchService stickerSearchService,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchStickers,
        MyTelegram.Schema.Messages.IFoundStickers>
{
    /// <summary>
    /// TDLib asks for the <a href="https://corefork.telegram.org/api/emoji-categories">emojiGroupPremium</a>
    /// category by searching for this sentinel emoji pair rather than by a flag
    /// (<c>StickersManager::do_get_premium_stickers</c>). It normalises emoji modifiers away first, so the
    /// variation selector may or may not survive — accept both forms.
    /// </summary>
    private static readonly string[] PremiumMagicEmoticons = ["\U0001F4C2⭐️", "\U0001F4C2⭐"];

    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Messages.IFoundStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSearchStickers obj)
    {
        var emoticon = obj.Emoticon?.Trim() ?? string.Empty;
        var query = obj.Q?.Trim() ?? string.Empty;
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : DefaultLimit;
        var offset = Math.Max(0, obj.Offset);

        var matches = PremiumMagicEmoticons.Contains(emoticon, StringComparer.Ordinal)
            ? await stickerSearchService.FindPremiumAsync(obj.Emojis)
            : await FindAsync(emoticon, query, obj.Emojis);

        var page = matches.Skip(offset).Take(limit).ToList();
        var documents = await stickerSetMapper.BuildDocumentsAsync(input, page);
        var returnedIds = documents.OfType<TDocument>().Select(p => p.Id).ToList();

        var hash = VectorHashHelper.ComputeHash(returnedIds);
        var nextOffset = matches.Count > offset + page.Count ? offset + page.Count : (int?)null;

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Messages.TFoundStickersNotModified { NextOffset = nextOffset };
        }

        return new MyTelegram.Schema.Messages.TFoundStickers
        {
            Hash = hash,
            Stickers = new TVector<IDocument>(documents),
            NextOffset = nextOffset
        };
    }

    /// <summary>
    /// An exact emoji match ranks above a keyword match: the emoji is what the user picked from the
    /// category bar, while the keywords are a fuzzy free-text index.
    /// </summary>
    private async Task<List<long>> FindAsync(string emoticon, string query, bool emojiSets)
    {
        var result = new List<long>();

        // A space-separated list of emoji is one query, so every one of them contributes.
        foreach (var single in emoticon.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            result.AddRange(await stickerSearchService.FindByEmoticonAsync(single, emojiSets));
        }

        if (query.Length > 0)
        {
            result.AddRange(await stickerSearchService.FindByKeywordAsync(query, emojiSets));
        }

        return result.Distinct().ToList();
    }
}
