using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Document;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Registers a silent MPEG4 the server produced as a real document, so it can be sent, downloaded
/// and saved like any other file.
/// </summary>
public interface IGifDocumentPublisher
{
    /// <summary>
    /// Publishes <paramref name="path"/> as an MPEG4 animation owned by <paramref name="userId"/>.
    /// Returns null when the body could not be stored.
    /// </summary>
    Task<TDocument?> PublishAsync(long userId, string path, string fileName, VideoInfo? info,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>The body goes into the object store the file server reads from, and the document itself is
/// written as a read model row. Neither of the two paths a client upload would take works for a file
/// the server produced:</para>
/// <list type="bullet">
/// <item><description><c>SaveMedia</c> with <c>inputMediaUploadedDocument</c> merges the parts of an
/// upload the file server itself received through <c>upload.saveFilePart</c>, which it keeps in its own
/// upload directory. Parts staged anywhere else are invisible to it, and it answers
/// <c>messageMediaEmpty</c> — measured against the running service.</description></item>
/// <item><description>The gRPC <c>CreateDocument</c> shortcut hardcodes sticker attributes: a document
/// created through it comes back with <c>documentAttributeImageSize(512, 512)</c> and
/// <c>documentAttributeSticker</c>, never <c>documentAttributeAnimated</c> — also measured. A GIF
/// without that attribute is refused by every client's saved-GIF list.</description></item>
/// </list>
/// <para>So the row is written here. It is the same deliberate exception as the web file row in
/// <see cref="WebFiles.IWebFileRegistrar"/>: the aggregate that owns <c>eventflow-documentreadmodel</c>
/// lives in the closed file server, this repository's <c>DocumentAggregate</c> is a stub, and there is
/// no command that would create a document with arbitrary attributes.</para>
/// </remarks>
public class GifDocumentPublisher(
    IMongoDatabase mongoDatabase,
    IStoredFileStorage storedFileStorage,
    IGifDocumentReader documentReader,
    IVideoTranscoder videoTranscoder,
    ILogger<GifDocumentPublisher> logger)
    : IGifDocumentPublisher, ITransientDependency
{
    private const string DocumentCollectionName = "eventflow-documentreadmodel";

    /// <summary>Longer side of the still preview, and the size type clients ask for it under.</summary>
    private const int ThumbMaxSize = 320;

    private const string ThumbSizeType = "m";

    public async Task<TDocument?> PublishAsync(long userId, string path, string fileName, VideoInfo? info,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            logger.LogWarning("There was nothing to publish for the animation {FileName}", fileName);

            return null;
        }

        var fileId = GenerateId();

        try
        {
            await storedFileStorage.UploadFileAsync(fileId, path, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The body of the animation {FileName} could not be stored", fileName);

            return null;
        }

        var thumb = await PublishThumbnailAsync(fileId, path, info, cancellationToken);

        await WriteDocumentAsync(userId, fileId, fileName, file.Length, info, thumb, cancellationToken);

        // Read back rather than mapped from what was written: the send path and the saved-GIF list both
        // read the document from here, so a row they cannot read is worth finding out about now.
        var stored = await documentReader.GetAsync(fileId, cancellationToken);
        if (stored == null)
        {
            logger.LogWarning("The document for the animation {FileName} could not be read back", fileName);

            return null;
        }

        logger.LogInformation("Published the animation {FileName} as document {DocumentId}", fileName, fileId);

        return documentReader.Map(stored);
    }

    /// <summary>
    /// Stores the still preview as the <c>{fileId}_m</c> object the file server serves for a thumbnail,
    /// and returns its dimensions. Null when there is no ffmpeg or it produced nothing — the animation is
    /// still published, it just has no preview to show while it downloads.
    /// </summary>
    private async Task<(int Width, int Height, int Size)?> PublishThumbnailAsync(long fileId, string path,
        VideoInfo? info, CancellationToken cancellationToken)
    {
        var thumbPath = Path.Combine(Path.GetTempPath(), $"gif-thumb-{fileId}-{Guid.NewGuid():N}.jpg");

        try
        {
            if (!await videoTranscoder.ExtractThumbnailAsync(path, thumbPath, ThumbMaxSize, cancellationToken))
            {
                return null;
            }

            await storedFileStorage.UploadFileAsync(fileId, thumbPath, cancellationToken, ThumbSizeType);

            var size = (int)new FileInfo(thumbPath).Length;
            var probed = await videoTranscoder.ProbeAsync(thumbPath, cancellationToken);
            if (probed is { Width: > 0, Height: > 0 })
            {
                return (probed.Width, probed.Height, size);
            }

            // ffprobe could not read the JPEG back: the frame was scaled to fit the box, so the source
            // dimensions still describe its shape.
            return info is { Width: > 0, Height: > 0 }
                ? Scale(info.Width, info.Height, size)
                : (ThumbMaxSize, ThumbMaxSize, size);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The preview of animation {DocumentId} could not be stored", fileId);

            return null;
        }
        finally
        {
            GifTempFile.Delete(thumbPath);
        }
    }

    private static (int Width, int Height, int Size) Scale(int width, int height, int size)
    {
        var longest = Math.Max(width, height);
        if (longest <= ThumbMaxSize)
        {
            return (width, height, size);
        }

        var factor = (double)ThumbMaxSize / longest;

        return (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)),
            size);
    }

    private Task WriteDocumentAsync(long userId, long fileId, string fileName, long size, VideoInfo? info,
        (int Width, int Height, int Size)? thumb, CancellationToken cancellationToken)
    {
        var attributes = new List<IDocumentAttribute> { new TDocumentAttributeAnimated() };

        if (info is { Width: > 0, Height: > 0 })
        {
            attributes.Add(new TDocumentAttributeVideo
            {
                W = info.Width,
                H = info.Height,
                Duration = info.DurationSeconds,
                Nosound = true,
                SupportsStreaming = true
            });
        }

        attributes.Add(new TDocumentAttributeFilename { FileName = fileName });

        var document = new BsonDocument
        {
            // The same identity the file server derives, so a document it later touches is the same row.
            ["_id"] = DocumentId.Create(fileId).Value,
            ["DocumentId"] = fileId,
            ["AccessHash"] = Random.Shared.NextInt64() & long.MaxValue,
            ["CreatorId"] = userId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // Bodies the server stores itself are unencrypted and live on the media DC, like the ones
            // created for sticker sets.
            ["DcId"] = MyTelegramConsts.MediaDcId,
            // Non-empty on purpose: a client that receives a document with an empty file_reference treats
            // it as stale and tries to refresh it through the message it came from before downloading
            // anything, and a GIF then sits at a spinner forever. Nothing here validates the value.
            ["FileReference"] = new BsonBinaryData(GenerateFileReference()),
            ["MimeType"] = GifDocumentHelper.Mp4MimeType,
            ["Name"] = fileName,
            ["Size"] = size,
            ["Attributes"] = BsonNull.Value,
            ["Attributes2"] = new BsonArray(attributes.Select(p =>
                p.ToBsonDocument<IDocumentAttribute>())),
            // The still preview a client draws while the animation downloads.
            ["Thumbs"] = thumb == null
                ? BsonNull.Value
                : new BsonArray(new[]
                {
                    new BsonDocument
                    {
                        ["W"] = thumb.Value.Width,
                        ["H"] = thumb.Value.Height,
                        ["Size"] = thumb.Value.Size,
                        ["Type"] = ThumbSizeType,
                        ["StrippedThumb"] = BsonNull.Value,
                        ["Bytes"] = BsonNull.Value
                    }
                }),
            ["VideoThumbs"] = BsonNull.Value,
            ["ThumbId"] = BsonNull.Value,
            ["VideoThumbId"] = BsonNull.Value,
            ["Fingerprint"] = BsonNull.Value,
            ["Md5CheckSum"] = BsonNull.Value,
            ["Version"] = 1L
        };

        return mongoDatabase.GetCollection<BsonDocument>(DocumentCollectionName).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    private static long GenerateId()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        bytes[0] &= 0x7F;

        return BitConverter.ToInt64(bytes, 0) & 0x7FFFFFFFFFFFFFFF;
    }

    private static byte[] GenerateFileReference()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);

        return bytes;
    }
}
