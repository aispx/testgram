namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// The server-side half of "uploading a GIF": converts an animation to MPEG4 when needed and adds
/// the result to the sender's saved GIFs.
///
/// <para>"Uploading a GIF will automatically add it to the saved gifs list." Clients already add it
/// to their <i>local</i> list when the message is sent, without calling <c>messages.saveGif</c>, so
/// without this the entry silently disappears the next time the list is refetched.</para>
/// See https://corefork.telegram.org/api/gifs
/// </summary>
public interface ISentGifProcessor
{
    /// <summary>
    /// Inspects outgoing media. Returns the media to actually send — the same object for anything
    /// that is not an animation, or a replacement carrying the converted MPEG4 document.
    /// </summary>
    Task<IMessageMedia?> ProcessAsync(IRequestInput input, IMessageMedia? media,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SentGifProcessor(
    IGifTranscodeService transcodeService,
    ISavedGifStore savedGifStore,
    ISavedGifLimitResolver limitResolver,
    ISavedGifUpdateNotifier updateNotifier,
    ILogger<SentGifProcessor> logger)
    : ISentGifProcessor, ITransientDependency
{
    public async Task<IMessageMedia?> ProcessAsync(IRequestInput input, IMessageMedia? media,
        CancellationToken cancellationToken = default)
    {
        var document = GifDocumentHelper.GetDocument(media);
        if (!GifDocumentHelper.HasAnimatedAttribute(document))
        {
            return media;
        }

        var result = media;

        // An image/gif upload is not a GIF to any client until it is MPEG4.
        var converted = await transcodeService.EnsureMp4Async(input.UserId, document!, cancellationToken);
        if (converted != null)
        {
            result = new TMessageMediaDocument { Document = converted };
            document = converted;
        }

        // Only the real thing goes into the list; a conversion that could not be done leaves the
        // message intact but is not saved, rather than storing something clients would discard.
        if (!GifDocumentHelper.IsAnimatedMp4(document))
        {
            return result;
        }

        try
        {
            var limit = await limitResolver.GetLimitAsync(input.UserId);
            await savedGifStore.AddAsync(input.UserId, document!.Id, limit, cancellationToken);
            await updateNotifier.NotifyAsync(input.UserId, input.AuthKeyId);
        }
        catch (Exception ex)
        {
            // Saving is a side effect of sending; it must never fail the send itself.
            logger.LogError(ex, "Adding sent GIF {DocumentId} to the saved list of {UserId} failed",
                document!.Id, input.UserId);
        }

        return result;
    }
}
