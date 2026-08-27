using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get recent stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getRecentStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRecentStickersHandler(
    IStickerDocumentListStore listStore,
    IStickerSetMapper stickerSetMapper,
    IStickerLimitResolver limitResolver)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentStickers,
        MyTelegram.Schema.Messages.IRecentStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IRecentStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetRecentStickers obj)
    {
        var entries = await listStore.GetAsync(StickerDocumentListKind.Recent, input.UserId, obj.Attached,
            limitResolver.GetRecentLimit());

        var documentIds = entries.ConvertAll(p => p.DocumentId);
        var documents = await stickerSetMapper.BuildDocumentsAsync(input, documentIds);
        var presentIds = documents.OfType<TDocument>().Select(p => p.Id).ToHashSet();

        if (presentIds.Count != documentIds.Count)
        {
            await listStore.RemoveManyAsync(StickerDocumentListKind.Recent, input.UserId,
                documentIds.Where(p => !presentIds.Contains(p)).ToList(), obj.Attached);
        }

        // dates is positional: entry n describes sticker n, so it has to be filtered exactly as the
        // documents were.
        var kept = entries.Where(p => presentIds.Contains(p.DocumentId)).ToList();
        var hash = VectorHashHelper.ComputeHash(kept.Select(p => p.DocumentId));

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Messages.TRecentStickersNotModified();
        }

        return new MyTelegram.Schema.Messages.TRecentStickers
        {
            Hash = hash,
            Stickers = new TVector<IDocument>(documents),
            Dates = new TVector<int>(kept.Select(p => p.Date)),
            Packs = new TVector<IStickerPack>(
                await stickerSetMapper.BuildPacksForDocumentsAsync(kept.ConvertAll(p => p.DocumentId)))
        };
    }
}
