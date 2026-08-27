using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get faved stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFavedStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetFavedStickersHandler(
    IStickerDocumentListStore listStore,
    IStickerSetMapper stickerSetMapper,
    IStickerLimitResolver limitResolver)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFavedStickers,
        MyTelegram.Schema.Messages.IFavedStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IFavedStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetFavedStickers obj)
    {
        var limit = await limitResolver.GetFavedLimitAsync(input.UserId);
        var entries = await listStore.GetAsync(StickerDocumentListKind.Faved, input.UserId, false, limit);
        var documentIds = entries.ConvertAll(p => p.DocumentId);

        var documents = await stickerSetMapper.BuildDocumentsAsync(input, documentIds);
        var presentIds = documents.OfType<TDocument>().Select(p => p.Id).ToList();

        // A favourite whose document has since disappeared would make the client's list shorter than ours
        // and its hash permanently different, so drop it here rather than send it.
        if (presentIds.Count != documentIds.Count)
        {
            await listStore.RemoveManyAsync(StickerDocumentListKind.Faved, input.UserId,
                documentIds.Except(presentIds).ToList(), false);
        }

        var hash = VectorHashHelper.ComputeHash(presentIds);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Messages.TFavedStickersNotModified();
        }

        return new MyTelegram.Schema.Messages.TFavedStickers
        {
            Hash = hash,
            Stickers = new TVector<IDocument>(documents),
            Packs = new TVector<IStickerPack>(await stickerSetMapper.BuildPacksForDocumentsAsync(presentIds))
        };
    }
}
