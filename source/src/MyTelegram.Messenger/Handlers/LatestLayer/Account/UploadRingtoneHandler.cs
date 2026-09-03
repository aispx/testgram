using MongoDB.Driver;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Services.Ringtones;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Upload notification sound, use <a href="https://corefork.telegram.org/method/account.saveRingtone">account.saveRingtone</a> to convert it and add it to the list of saved notification sounds.
/// Possible errors
/// Code Type Description
/// 400 RINGTONE_MIME_INVALID The MIME type for the ringtone is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.uploadRingtone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Supported formats: MP3, OGG OPUS
///
/// <para>This used to write a row into <c>eventflow-documentreadmodel</c> by hand and never move the body
/// the client had uploaded, so <c>upload.getFile</c> on the result found nothing: the sound could not be
/// played by anyone. <c>Size</c> was <c>parts × 512 KB</c>, the duration was the constant 5, and the
/// <c>dc_id</c> disagreed with what the read path reported. The document therefore goes through
/// <see cref="IMediaHelper.SaveMediaAsync"/> now — the same gRPC route <c>messages.sendMedia</c>,
/// <c>messages.uploadMedia</c> and <c>account.uploadWallPaper</c> take, where the file server merges the
/// uploaded parts, owns the row and reports the real size.</para>
///
/// <para><b>The upload also saves.</b> Neither Android (<c>RingtoneUploader</c> →
/// <c>RingtoneDataStore.onRingtoneUploaded</c>), nor tdesktop (<c>Api::Ringtones::ready</c>, which inserts
/// the document into its own list), nor iOS (<c>_internal_uploadRingtone</c>, <c>[item] + sounds</c>) calls
/// <c>account.saveRingtone</c> afterwards — all three assume the server already keeps it. Without that, the
/// sound disappears from Android the next time it refreshes the list from the server. tdlib does call
/// <c>saveRingtone</c> explicitly, which is why that method has to be idempotent.</para>
/// </remarks>
internal sealed class UploadRingtoneHandler(
    IMongoDatabase database,
    IMediaHelper mediaHelper,
    IRingtoneAudioProbe audioProbe,
    IStoredFileStorage storedFileStorage,
    IRingtoneLimits limits,
    ISavedRingtoneStore savedRingtoneStore,
    ISavedRingtoneUpdateNotifier updateNotifier,
    ILogger<UploadRingtoneHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUploadRingtone, MyTelegram.Schema.IDocument>
{
    protected override async Task<MyTelegram.Schema.IDocument> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestUploadRingtone obj)
    {
        // "Supported formats: MP3, OGG OPUS" — the only error this method documents.
        if (!RingtoneMimeTypes.IsUploadable(obj.MimeType))
        {
            logger.LogWarning("Refused a notification sound with the MIME type {MimeType}", obj.MimeType);
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();
        }

        // Both upload routes are legal, and an empty file_name is not a MIME problem: fall back to the
        // name the InputFile carries rather than refusing the upload over it.
        var fileName = FirstNonEmpty(obj.FileName, FileNameOf(obj.File)) ?? DefaultFileName(obj.MimeType);

        if (obj.File is not (MyTelegram.Schema.TInputFile or MyTelegram.Schema.TInputFileBig))
        {
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();
        }

        var maxSize = limits.MaxSizeBytes;

        // The staged parts are the only way to know the duration *before* the document exists, so that the
        // audio attribute can travel to the file server with the rest. They are not always readable: the file
        // server keeps its own copy of an upload and on this deployment nothing lands in this repository's
        // file_parts, in which case the body is read back from the object store after the document is created.
        var body = await UploadedFileReader.ReadAsync(database, input.UserId, obj.File, maxSize + 1L);

        var info = body == null ? null : await ProbeAsync(body, fileName);

        if (body != null && body.LongLength > maxSize)
        {
            // Telegram Android matches this string literally and formats its own message with the limit
            // from appConfig; anything else is "an unknown error occurred" for a file that is merely big.
            RingtoneExtraRpcErrors.RingtoneSizeTooBig.ThrowRpcError();
        }

        if (info != null && info.DurationSeconds > limits.MaxDurationSeconds)
        {
            RingtoneExtraRpcErrors.RingtoneDurationTooLong.ThrowRpcError();
        }

        var attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>
        {
            new MyTelegram.Schema.TDocumentAttributeFilename { FileName = fileName }
        };

        if (info != null)
        {
            // Not a voice note: a notification sound is played by the client's own tone player.
            attributes.Add(new MyTelegram.Schema.TDocumentAttributeAudio
            {
                Voice = false,
                Duration = info.DurationSeconds,
                Title = info.Title,
                Performer = info.Performer
            });
        }

        var media = await mediaHelper.SaveMediaAsync(new MyTelegram.Schema.TInputMediaUploadedDocument
        {
            File = obj.File,
            MimeType = obj.MimeType,
            Attributes = attributes
        });

        if (media is not MyTelegram.Schema.TMessageMediaDocument
            {
                Document: MyTelegram.Schema.TDocument document
            })
        {
            logger.LogWarning("The file server did not create a document for the notification sound {FileName}",
                fileName);
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();

            return null!;
        }

        if (document.Size > maxSize)
        {
            // The staged parts were not readable, so the size only became known once the file server had
            // merged them. Still the advertised limit, still the string the client knows.
            RingtoneExtraRpcErrors.RingtoneSizeTooBig.ThrowRpcError();
        }

        // Second chance at the duration, now that the body is in the object store: this is the path that
        // actually runs on a deployment whose upload parts the messenger never sees.
        info ??= await ProbeStoredAsync(document.Id, fileName);

        if (info != null && info.DurationSeconds > limits.MaxDurationSeconds)
        {
            RingtoneExtraRpcErrors.RingtoneDurationTooLong.ThrowRpcError();
        }

        var added = await savedRingtoneStore.AddAsync(input.UserId, document.Id, limits.MaxSavedCount,
            info: info);
        if (added)
        {
            await updateNotifier.NotifyAsync(input.UserId, input.PermAuthKeyId);
        }

        logger.LogInformation(
            "Saved the notification sound {FileName} as document {DocumentId} for user {UserId} ({Size} bytes, " +
            "{Duration}s)",
            fileName, document.Id, input.UserId, document.Size, info?.DurationSeconds ?? 0);

        return info == null
            ? document
            : RingtoneAudioAttribute.Merge(document, info.DurationSeconds, info.Title, info.Performer);
    }

    /// <summary>
    /// Duration and tags of the body the file server stored, read back through the object store — the same
    /// route <c>GifTranscodeService</c> and <c>RingtoneConverter</c> take, which decrypts a client upload with
    /// the key from <c>eventflow-filereadmodel</c>.
    /// </summary>
    private async Task<RingtoneAudioInfo?> ProbeStoredAsync(long documentId, string fileName)
    {
        if (!audioProbe.IsAvailable)
        {
            logger.LogWarning(
                "ffprobe is unavailable, so the duration of the notification sound {FileName} is unknown and " +
                "ringtone_duration_max cannot be enforced for it", fileName);

            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"ringtone-stored-{documentId}-{Guid.NewGuid():N}");

        try
        {
            if (!await storedFileStorage.DownloadToFileAsync(documentId, path))
            {
                logger.LogWarning(
                    "The body of the notification sound {FileName} could not be read back, so its duration is " +
                    "unknown and ringtone_duration_max cannot be enforced for it", fileName);

                return null;
            }

            return await audioProbe.ProbeAsync(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The notification sound {FileName} could not be probed after upload", fileName);

            return null;
        }
        finally
        {
            RingtoneTempFile.Delete(path);
        }
    }

    /// <summary>
    /// Duration and tags of the body a client staged in <c>file_parts</c>, when this repository can see it.
    /// Returns null when ffprobe is not installed — the alternative would be inventing a number, and a sound
    /// with a wrong duration is worse than one with none.
    /// </summary>
    private async Task<RingtoneAudioInfo?> ProbeAsync(byte[] body, string fileName)
    {
        if (!audioProbe.IsAvailable)
        {
            logger.LogWarning(
                "ffprobe is unavailable, so the duration of the notification sound {FileName} is unknown and " +
                "ringtone_duration_max cannot be enforced for it", fileName);

            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"ringtone-upload-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllBytesAsync(path, body);

            return await audioProbe.ProbeAsync(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "The notification sound {FileName} could not be staged for probing", fileName);

            return null;
        }
        finally
        {
            RingtoneTempFile.Delete(path);
        }
    }

    private static string? FileNameOf(MyTelegram.Schema.IInputFile file)
    {
        return file switch
        {
            MyTelegram.Schema.TInputFile inputFile => inputFile.Name,
            MyTelegram.Schema.TInputFileBig inputFileBig => inputFileBig.Name,
            _ => null
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
    }

    private static string DefaultFileName(string? mimeType)
    {
        return RingtoneMimeTypes.IsMp3(mimeType) ? "ringtone.mp3" : "ringtone.ogg";
    }
}
