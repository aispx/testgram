using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Document;
using MyTelegram.Messenger.Services.Documents;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// Turns a saved sound that is not MP3 into one, as <c>account.saveRingtone</c> is documented to do:
/// "If the notification sound is already in MP3 format, account.savedRingtone will be returned.
/// Otherwise, it will be automatically converted and a account.savedRingtoneConverted will be returned,
/// containing a new document object that should be used to refer to the ringtone from now on (ie when
/// deleting it using the unsave parameter, or when downloading it)."
///
/// <para>The conversion is what makes an existing voice message usable as a notification sound on every
/// platform: iOS can only play a narrow set of formats for a notification, which is why the official
/// service normalises everything to MP3 rather than storing the OGG OPUS it was given.</para>
/// See https://corefork.telegram.org/api/ringtones#uploading-notification-sounds
/// </summary>
public interface IRingtoneConverter
{
    /// <summary>
    /// The MP3 twin of <paramref name="document"/>, converting and publishing it if necessary. Returns
    /// null when the sound is already MP3, or when the conversion could not be done — the caller then
    /// saves the sound as it is, which is worse than MP3 but not a dead entry in the list.
    /// </summary>
    Task<TDocument?> EnsureMp3Async(long userId, IDocumentReadModel document,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// The row is written here rather than through the file server, for the same reason
/// <c>GifDocumentPublisher</c> does it: <c>SaveMedia</c> only merges parts the file server itself
/// received through <c>upload.saveFilePart</c>, and its <c>CreateDocument</c> shortcut hardcodes sticker
/// attributes — a sound created through it would come back as a 512×512 sticker with no
/// <c>documentAttributeAudio</c>. The aggregate that owns <c>eventflow-documentreadmodel</c> lives in the
/// closed file server and this repository's <c>DocumentAggregate</c> is a stub, so there is no command
/// that would create a document with arbitrary attributes.
/// </remarks>
public class RingtoneConverter(
    IMongoDatabase mongoDatabase,
    IStoredFileStorage storedFileStorage,
    IRingtoneAudioProbe audioProbe,
    IRingtoneMp3ConversionStore conversionStore,
    IDocumentReader documentReader,
    ILogger<RingtoneConverter> logger)
    : IRingtoneConverter, ITransientDependency
{
    private const string DocumentCollectionName = "eventflow-documentreadmodel";

    public async Task<TDocument?> EnsureMp3Async(long userId, IDocumentReadModel document,
        CancellationToken cancellationToken = default)
    {
        if (RingtoneMimeTypes.IsMp3(document.MimeType))
        {
            return null;
        }

        // The same sound saved twice must resolve to the same twin, or the list would hold two entries
        // for one sound and the client's own id would stop matching either.
        var existingId = await conversionStore.GetMp3DocumentIdAsync(document.DocumentId, cancellationToken);
        if (existingId.HasValue)
        {
            var existing = await documentReader.GetAsync(existingId.Value, cancellationToken);
            if (existing != null && RingtoneMimeTypes.IsMp3(existing.MimeType))
            {
                return documentReader.Map(existing);
            }
        }

        if (!audioProbe.IsAvailable)
        {
            // FfmpegLocator already logged why. Saving the sound unconverted is the honest outcome: it
            // still plays on Android and tdesktop, it is simply not the MP3 the API promises.
            logger.LogWarning(
                "The notification sound {DocumentId} is {MimeType} and ffmpeg is unavailable, so it cannot " +
                "be converted to MP3; it will be saved as it is.",
                document.DocumentId, document.MimeType);

            return null;
        }

        var sourcePath = Path.Combine(Path.GetTempPath(), $"ringtone-source-{document.DocumentId}-{Guid.NewGuid():N}");
        var destinationPath = Path.ChangeExtension(sourcePath, ".mp3");

        try
        {
            if (!await storedFileStorage.DownloadToFileAsync(document.DocumentId, sourcePath, cancellationToken))
            {
                logger.LogWarning("The body of the notification sound {DocumentId} could not be read back for " +
                                  "conversion", document.DocumentId);

                return null;
            }

            if (!await audioProbe.ConvertToMp3Async(sourcePath, destinationPath, cancellationToken))
            {
                return null;
            }

            var info = await audioProbe.ProbeAsync(destinationPath, cancellationToken);
            var fileName = Path.GetFileNameWithoutExtension(SourceFileName(document)) + ".mp3";

            var converted = await PublishAsync(userId, destinationPath, fileName, info, cancellationToken);
            if (converted == null)
            {
                return null;
            }

            await conversionStore.SaveAsync(document.DocumentId, converted.Id, cancellationToken);

            return converted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The notification sound {DocumentId} could not be converted to MP3",
                document.DocumentId);

            return null;
        }
        finally
        {
            RingtoneTempFile.Delete(sourcePath);
            RingtoneTempFile.Delete(destinationPath);
        }
    }

    private async Task<TDocument?> PublishAsync(long userId, string path, string fileName,
        RingtoneAudioInfo? info, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            return null;
        }

        var fileId = GenerateId();

        try
        {
            await storedFileStorage.UploadFileAsync(fileId, path, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The body of the converted notification sound {FileName} could not be stored",
                fileName);

            return null;
        }

        await WriteDocumentAsync(userId, fileId, fileName, file.Length, info, cancellationToken);

        // Read back rather than mapped from what was written: the list and upload.getFile both read the
        // document from here, so a row they cannot read is worth finding out about now.
        var stored = await documentReader.GetAsync(fileId, cancellationToken);
        if (stored == null)
        {
            logger.LogWarning("The document for the converted notification sound {FileName} could not be read back",
                fileName);

            return null;
        }

        logger.LogInformation("Published the converted notification sound {FileName} as document {DocumentId}",
            fileName, fileId);

        return documentReader.Map(stored);
    }

    private Task WriteDocumentAsync(long userId, long fileId, string fileName, long size, RingtoneAudioInfo? info,
        CancellationToken cancellationToken)
    {
        var attributes = new List<IDocumentAttribute>
        {
            // Not a voice note: a notification sound is played by the client's own tone player, and a
            // voice flag would put it in the wrong place in every UI that groups audio.
            new TDocumentAttributeAudio
            {
                Voice = false,
                Duration = info?.DurationSeconds ?? 0,
                Title = info?.Title,
                Performer = info?.Performer
            },
            new TDocumentAttributeFilename { FileName = fileName }
        };

        var document = new BsonDocument
        {
            // The same identity the file server derives, so a document it later touches is the same row.
            ["_id"] = DocumentId.Create(fileId).Value,
            ["DocumentId"] = fileId,
            ["AccessHash"] = Random.Shared.NextInt64() & long.MaxValue,
            ["CreatorId"] = userId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // Bodies the server stores itself are unencrypted and live on the media DC.
            ["DcId"] = MyTelegramConsts.MediaDcId,
            // No FileReference is stored: references are derived from the document id on the way out
            // (IFileReferenceStamper). See https://corefork.telegram.org/api/file-references
            ["MimeType"] = RingtoneMimeTypes.Mp3,
            ["Name"] = fileName,
            ["Size"] = size,
            ["Attributes"] = BsonNull.Value,
            ["Attributes2"] = new BsonArray(attributes.Select(p => p.ToBsonDocument<IDocumentAttribute>())),
            ["Thumbs"] = BsonNull.Value,
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

    private static string SourceFileName(IDocumentReadModel document)
    {
        if (!string.IsNullOrWhiteSpace(document.Name))
        {
            return document.Name;
        }

        return $"ringtone-{document.DocumentId}";
    }

    private static long GenerateId()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        bytes[0] &= 0x7F;

        return BitConverter.ToInt64(bytes, 0) & 0x7FFFFFFFFFFFFFFF;
    }
}

internal static class RingtoneTempFile
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
            // A leftover temp file is not worth failing the request over.
        }
    }
}
