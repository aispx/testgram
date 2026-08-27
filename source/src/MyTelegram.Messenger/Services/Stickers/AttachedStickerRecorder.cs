namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Records the stickersets a client baked into an outgoing photo or video, and marks the media as
/// carrying them.
///
/// <para>Two things have to happen for the feature to work end to end: the sets have to be stored so
/// <c>messages.getAttachedStickers</c> can answer, and the media has to advertise that it has some —
/// <c>photo.has_stickers</c> or <c>documentAttributeHasStickers</c> — because that flag is what makes a
/// client offer the "view stickers" action at all.</para>
/// See https://corefork.telegram.org/api/stickers#attached-stickers
/// </summary>
public interface IAttachedStickerRecorder
{
    /// <summary>
    /// Returns the media to actually send: the same object when nothing was attached, or one marked as
    /// carrying stickers.
    /// </summary>
    Task<IMessageMedia?> ProcessAsync(IInputMedia? inputMedia, IMessageMedia? media,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class AttachedStickerRecorder(
    IStickerSetStore stickerSetStore,
    IAttachedStickerStore attachedStickerStore,
    ILogger<AttachedStickerRecorder> logger) : IAttachedStickerRecorder, ITransientDependency
{
    public async Task<IMessageMedia?> ProcessAsync(IInputMedia? inputMedia, IMessageMedia? media,
        CancellationToken cancellationToken = default)
    {
        var stickers = ReadInputStickers(inputMedia);
        if (stickers.Count == 0)
        {
            return media;
        }

        try
        {
            var stickerSetIds = new List<long>();
            foreach (var documentId in stickers)
            {
                var setDocument = await stickerSetStore.FindByDocumentIdAsync(documentId, cancellationToken);
                if (setDocument != null)
                {
                    stickerSetIds.Add(setDocument.GetInt64("StickerSetId"));
                }
            }

            if (stickerSetIds.Count == 0)
            {
                return media;
            }

            switch (media)
            {
                case TMessageMediaPhoto { Photo: TPhoto photo }:
                    await attachedStickerStore.SaveAsync(AttachedStickersDocument.MakePhotoId(photo.Id),
                        stickerSetIds, cancellationToken);
                    photo.HasStickers = true;

                    return media;

                case TMessageMediaDocument { Document: TDocument document }:
                    await attachedStickerStore.SaveAsync(AttachedStickersDocument.MakeDocumentId(document.Id),
                        stickerSetIds, cancellationToken);

                    document.Attributes ??= [];
                    if (!document.Attributes.OfType<TDocumentAttributeHasStickers>().Any())
                    {
                        document.Attributes.Add(new TDocumentAttributeHasStickers());
                    }

                    return media;

                default:
                    return media;
            }
        }
        catch (Exception exception)
        {
            // Losing the sticker attribution must not cost the user their message.
            logger.LogWarning(exception, "Failed to record attached stickers");

            return media;
        }
    }

    /// <summary>
    /// The <c>stickers</c> field of the two uploaded-media constructors. Only those two carry it: media sent
    /// by id was already recorded when it was first uploaded.
    /// </summary>
    private static List<long> ReadInputStickers(IInputMedia? inputMedia)
    {
        var stickers = inputMedia switch
        {
            TInputMediaUploadedPhoto photo => photo.Stickers,
            TInputMediaUploadedDocument document => document.Stickers,
            _ => null
        };

        if (stickers == null)
        {
            return [];
        }

        return stickers.OfType<TInputDocument>().Select(p => p.Id).Distinct().ToList();
    }
}
