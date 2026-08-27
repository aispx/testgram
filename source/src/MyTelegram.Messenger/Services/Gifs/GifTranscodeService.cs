using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Turns an uploaded animation that is not yet MPEG4 into one.
///
/// <para>"On Telegram, GIFs are actually MPEG4 videos without sound; if the user tries to upload an
/// actual GIF file, it will be automatically converted to an MPEG4 file by the server." Without this
/// step an <c>image/gif</c> upload is not a GIF to any client: tdlib refuses to save it, tdesktop
/// drops it out of the saved list, and Android never offers it in the GIF tab.</para>
/// See https://corefork.telegram.org/api/gifs#uploading-gifs
/// </summary>
public interface IGifTranscodeService
{
    /// <summary>
    /// The MPEG4 twin of <paramref name="document"/>, converting it if necessary. Returns null when
    /// nothing needs to change, or when the conversion could not be done — the caller then sends the
    /// document as it is rather than replacing it with something broken.
    /// </summary>
    Task<TDocument?> EnsureMp4Async(long userId, TDocument document,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class GifTranscodeService(
    IStoredFileStorage storedFileStorage,
    IVideoTranscoder videoTranscoder,
    IFfmpegLocator ffmpegLocator,
    IGifDocumentPublisher documentPublisher,
    IGifMp4ConversionStore conversionStore,
    IGifDocumentReader documentReader,
    ILogger<GifTranscodeService> logger)
    : IGifTranscodeService, ITransientDependency
{
    public async Task<TDocument?> EnsureMp4Async(long userId, TDocument document,
        CancellationToken cancellationToken = default)
    {
        if (!GifDocumentHelper.NeedsMp4Conversion(document))
        {
            return null;
        }

        // The same GIF re-sent must not be transcoded again — and saveGif on the original id has to
        // resolve to the same MPEG4, otherwise the list would hold two entries for one animation.
        var existingId = await conversionStore.GetMp4DocumentIdAsync(document.Id, cancellationToken);
        if (existingId.HasValue)
        {
            var existing = await documentReader.GetAsync(existingId.Value, cancellationToken);
            if (GifDocumentHelper.IsAnimatedMp4(existing))
            {
                return documentReader.Map(existing!);
            }
        }

        if (!ffmpegLocator.IsAvailable)
        {
            // FfmpegLocator already logged why. Sending the original unchanged is the honest outcome:
            // it stays a viewable file, it just is not a GIF.
            logger.LogWarning(
                "Document {DocumentId} is an animation but not MPEG4, and ffmpeg is unavailable, so it " +
                "cannot be converted; it will be sent as-is and will not appear in saved GIFs.",
                document.Id);
            return null;
        }

        var sourcePath = Path.Combine(Path.GetTempPath(), $"gif-source-{document.Id}-{Guid.NewGuid():N}");
        var destinationPath = Path.ChangeExtension(sourcePath, ".mp4");

        try
        {
            if (!await storedFileStorage.DownloadToFileAsync(document.Id, sourcePath, cancellationToken))
            {
                logger.LogWarning("The body of animation {DocumentId} could not be read back for conversion",
                    document.Id);
                return null;
            }

            if (!await videoTranscoder.ConvertGifToMp4Async(sourcePath, destinationPath, cancellationToken))
            {
                return null;
            }

            var info = await videoTranscoder.ProbeAsync(destinationPath, cancellationToken);
            var fileName = Path.GetFileNameWithoutExtension(GetSourceFileName(document)) + ".mp4";
            var converted = await documentPublisher.PublishAsync(userId, destinationPath, fileName, info,
                cancellationToken);
            if (converted == null)
            {
                return null;
            }

            await conversionStore.SetAsync(document.Id, converted.Id, cancellationToken);

            logger.LogInformation("Converted animation {SourceId} ({Mime}) to MPEG4 document {Mp4Id}",
                document.Id, document.MimeType, converted.Id);

            return converted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Converting animation {DocumentId} to MPEG4 failed", document.Id);
            return null;
        }
        finally
        {
            GifTempFile.Delete(sourcePath);
            GifTempFile.Delete(destinationPath);
        }
    }

    private static string GetSourceFileName(TDocument document)
    {
        var name = document.Attributes?.OfType<TDocumentAttributeFilename>().FirstOrDefault()?.FileName;

        return string.IsNullOrWhiteSpace(name) ? "animation.gif" : name;
    }
}

/// <summary>Temp file cleanup shared by the conversion and Tenor import paths.</summary>
internal static class GifTempFile
{
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a send over.
        }
    }
}
