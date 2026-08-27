namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Resolves the media an inline result refers to.
///
/// <para>Bots send results that <i>reference</i> media — <c>inputBotInlineResultDocument</c> carries an
/// <c>inputDocument</c>, and a generic <c>inputBotInlineResult</c> carries a web document URL. Both the
/// answer the querying client receives and the message that is sent when a result is picked need the
/// resolved media, otherwise the client is handed a media result with no media in it and there is
/// nothing to render or send.</para>
/// </summary>
public interface IInlineResultMediaResolver
{
    /// <summary>
    /// The document a result points at, for the copy of the result that goes back to the client.
    /// Returns null for results that do not reference a stored document.
    /// </summary>
    Task<TDocument?> ResolveDocumentAsync(IInputBotInlineResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The media to attach to the message when <paramref name="result"/> is sent. A GIF found through
    /// Tenor search is imported into this server at this point, since only then does it need to exist
    /// as a document. Returns null when the result carries no sendable media, in which case the caller
    /// sends the text of <c>send_message</c> alone.
    /// </summary>
    Task<IMessageMedia?> ResolveSendMediaAsync(long userId, IInputBotInlineResult result,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class InlineResultMediaResolver(
    IGifDocumentReader documentReader,
    ITenorGifImporter tenorImporter,
    ILogger<InlineResultMediaResolver> logger)
    : IInlineResultMediaResolver, ITransientDependency
{
    public async Task<TDocument?> ResolveDocumentAsync(IInputBotInlineResult result,
        CancellationToken cancellationToken = default)
    {
        if (result is not TInputBotInlineResultDocument { Document: TInputDocument inputDocument })
        {
            return null;
        }

        var document = await documentReader.GetAsync(inputDocument.Id, cancellationToken);
        if (document == null)
        {
            logger.LogWarning("Inline result references document {DocumentId}, which does not exist",
                inputDocument.Id);
            return null;
        }

        return documentReader.Map(document);
    }

    public async Task<IMessageMedia?> ResolveSendMediaAsync(long userId, IInputBotInlineResult result,
        CancellationToken cancellationToken = default)
    {
        var document = await ResolveDocumentAsync(result, cancellationToken);
        if (document != null)
        {
            return new TMessageMediaDocument { Document = document };
        }

        // A GIF from Tenor search: the result only referenced Tenor's URL, so the animation becomes a
        // document of this server now that it is actually being sent. That is also what makes it
        // saveable afterwards, since the saved-GIF list holds document ids.
        if (result is TInputBotInlineResult { Content: TInputWebDocument content } webResult &&
            string.Equals(content.MimeType, GifDocumentHelper.Mp4MimeType, StringComparison.OrdinalIgnoreCase))
        {
            var imported = await tenorImporter.ImportAsync(userId, webResult.Id, content.Url,
                ReadVideoInfo(content), cancellationToken);

            return imported == null ? null : new TMessageMediaDocument { Document = imported };
        }

        // Photo results are not resolved: nothing in this codebase maps a photo read model to a TL
        // photo yet, so there is no honest way to attach one here.
        return null;
    }

    /// <summary>
    /// Dimensions and duration as the answering bot reported them on the web document, so the import
    /// does not have to spawn ffprobe while the sender waits. Null when they are missing or nonsense,
    /// which puts the probe back.
    /// </summary>
    private static VideoProcessing.VideoInfo? ReadVideoInfo(TInputWebDocument content)
    {
        var video = content.Attributes?.OfType<TDocumentAttributeVideo>().FirstOrDefault();
        if (video is not { W: > 0, H: > 0 })
        {
            return null;
        }

        return new VideoProcessing.VideoInfo(video.W, video.H, (int)Math.Ceiling(video.Duration), "h264");
    }
}
