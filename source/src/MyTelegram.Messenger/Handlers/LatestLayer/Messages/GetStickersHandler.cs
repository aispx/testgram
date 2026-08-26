using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get stickers by emoji
/// Possible errors
/// Code Type Description
/// 400 EMOTICON_EMPTY The emoji is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStickersHandler(
    IStickerSearchService stickerSearchService,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetStickers,
        MyTelegram.Schema.Messages.IStickers>
{
    /// <summary>
    /// "To fetch this special list, invoke messages.getStickers with emoticon=⭐️⭐️" — the Premium sticker
    /// examples shown where the UI advertises a subscription. Both spellings are accepted because clients
    /// differ on whether the variation selector survives their emoji normalisation.
    /// See https://corefork.telegram.org/api/stickers#premium-sticker-examples
    /// </summary>
    private static readonly string[] PremiumExampleEmoticons = ["⭐️⭐️", "⭐⭐"];

    protected override async Task<MyTelegram.Schema.Messages.IStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetStickers obj)
    {
        var emoticon = obj.Emoticon?.Trim() ?? string.Empty;
        if (emoticon.Length == 0)
        {
            RpcErrors.RpcErrors400.EmoticonEmpty.ThrowRpcError();
        }

        var documentIds = PremiumExampleEmoticons.Contains(emoticon, StringComparer.Ordinal)
            ? await stickerSearchService.FindPremiumAsync(false)
            : await stickerSearchService.FindByEmoticonAsync(emoticon, false);

        var documents = await stickerSetMapper.BuildDocumentsAsync(input, documentIds);

        // Hashed over what is actually returned, so a document that has since disappeared invalidates the
        // client's copy. Echoing the request hash back — which is what this did — told every client its
        // cache was current no matter what had changed.
        var hash = VectorHashHelper.ComputeHash(documents.OfType<TDocument>().Select(p => p.Id));

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Messages.TStickersNotModified();
        }

        return new MyTelegram.Schema.Messages.TStickers
        {
            Hash = hash,
            Stickers = new TVector<IDocument>(documents)
        };
    }
}
