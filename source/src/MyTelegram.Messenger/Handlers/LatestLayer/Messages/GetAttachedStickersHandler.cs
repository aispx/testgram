using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get stickers attached to a photo or video
/// Possible errors
/// Code Type Description
/// 400 MEDIA_EMPTY The provided media object is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAttachedStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAttachedStickersHandler(
    IAttachedStickerStore attachedStickerStore,
    IStickerSetStore stickerSetStore,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAttachedStickers,
        TVector<MyTelegram.Schema.IStickerSetCovered>>
{
    protected override async Task<TVector<IStickerSetCovered>> HandleCoreAsync(IRequestInput input,
        RequestGetAttachedStickers obj)
    {
        var id = obj.Media switch
        {
            TInputStickeredMediaPhoto { Id: TInputPhoto photo } =>
                AttachedStickersDocument.MakePhotoId(photo.Id),
            TInputStickeredMediaDocument { Id: TInputDocument document } =>
                AttachedStickersDocument.MakeDocumentId(document.Id),
            _ => null
        };

        if (id == null)
        {
            RpcErrors.RpcErrors400.MediaEmpty.ThrowRpcError();
        }

        var stickerSetIds = await attachedStickerStore.GetAsync(id!);
        if (stickerSetIds.Count == 0)
        {
            // Nothing was attached, or the media predates the feature. An empty vector is the honest answer:
            // clients only reach this method from the "view stickers" action, which they show when the media
            // claims to have some.
            return new TVector<IStickerSetCovered>();
        }

        var catalogue = await stickerSetStore.FindManyAsync(stickerSetIds);
        var setDocuments = stickerSetIds
            .Where(catalogue.ContainsKey)
            .Select(p => catalogue[p])
            .ToList();

        return new TVector<IStickerSetCovered>(
            await stickerSetMapper.BuildCoveredAsync(input, setDocuments, false));
    }
}
