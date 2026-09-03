using MyTelegram.Messenger.Services.Documents;
using MyTelegram.Messenger.Services.Ringtones;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Save or remove saved notification sound.
/// Possible errors
/// Code Type Description
/// 400 RINGTONE_INVALID The specified ringtone is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveRingtone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>"If the notification sound is already in MP3 format, <c>account.savedRingtone</c> will be
/// returned. Otherwise, it will be automatically converted and a <c>account.savedRingtoneConverted</c>
/// will be returned, containing a new document object that should be used to refer to the ringtone from
/// now on (ie when deleting it using the unsave parameter, or when downloading it)." This used to answer
/// <c>savedRingtoneConverted</c> with <b>the same</b> document it was given and a comment saying the
/// conversion would be needed in a real implementation.</para>
///
/// <para>The <c>file_reference</c> the client sends is deliberately <b>not</b> validated: this method
/// documents no <c>FILE_REFERENCE_*</c> error, and Android and iOS both quote one straight out of their
/// caches (<c>MediaDataController.saveToRingtones</c>, <c>_internal_saveRingtone</c>), so refusing a stale
/// one would break saving a sound whose only fault is an old cache. The access hash <i>is</i> validated,
/// because upstream access-hash checking does not cover this request type.</para>
/// </remarks>
internal sealed class SaveRingtoneHandler(
    ISavedRingtoneStore savedRingtoneStore,
    IRingtoneLimits limits,
    IRingtoneConverter ringtoneConverter,
    IRingtoneMp3ConversionStore conversionStore,
    ISavedRingtoneUpdateNotifier updateNotifier,
    IDocumentReader documentReader,
    IAccessHashHelper2 accessHashHelper,
    ILogger<SaveRingtoneHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveRingtone,
        MyTelegram.Schema.Account.ISavedRingtone>
{
    protected override async Task<MyTelegram.Schema.Account.ISavedRingtone> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestSaveRingtone obj)
    {
        if (obj.Id is not MyTelegram.Schema.TInputDocument inputDocument)
        {
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();

            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputDocument.Id, inputDocument.AccessHash,
            AccessHashType.Document);

        if (obj.Unsave)
        {
            return await UnsaveAsync(input, inputDocument.Id);
        }

        return await SaveAsync(input, inputDocument.Id);
    }

    /// <summary>
    /// Removes the sound. The id may be either the one the list holds or the one the client saved before
    /// the server converted it, so both are tried — a client that still refers to the original would
    /// otherwise be unable to delete the entry at all. Removing something that is not saved is not an
    /// error: tdlib and tdesktop treat a failure as "resync everything".
    /// </summary>
    private async Task<MyTelegram.Schema.Account.ISavedRingtone> UnsaveAsync(IRequestInput input, long documentId)
    {
        var removed = await savedRingtoneStore.RemoveAsync(input.UserId, documentId);

        var row = await savedRingtoneStore.FindAsync(input.UserId, documentId);
        if (row != null)
        {
            removed |= await savedRingtoneStore.RemoveAsync(input.UserId, row.DocumentId);
        }

        var convertedId = await conversionStore.GetMp3DocumentIdAsync(documentId);
        if (convertedId.HasValue)
        {
            removed |= await savedRingtoneStore.RemoveAsync(input.UserId, convertedId.Value);
        }

        if (removed)
        {
            await updateNotifier.NotifyAsync(input.UserId, input.PermAuthKeyId);
        }

        return new MyTelegram.Schema.Account.TSavedRingtone();
    }

    private async Task<MyTelegram.Schema.Account.ISavedRingtone> SaveAsync(IRequestInput input, long documentId)
    {
        var document = await documentReader.GetAsync(documentId);
        if (document == null)
        {
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();

            return null!;
        }

        // Anything may be addressed by an InputDocument — a sticker, a video, a photo's document. Only a
        // sound may become a notification sound, and the audio attribute is what every client reads the
        // duration of the tone from.
        if (!RingtoneMimeTypes.IsSaveable(document.MimeType) && !HasAudioAttribute(document))
        {
            logger.LogWarning("Refused document {DocumentId} ({MimeType}) as a notification sound",
                documentId, document.MimeType);
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();
        }

        if (document.Size > limits.MaxSizeBytes || DurationOf(document) > limits.MaxDurationSeconds)
        {
            // Android checks both of these itself before sending (saveToRingtones), so this is the backstop
            // for the clients that do not; RINGTONE_INVALID is the only error the method documents.
            RpcErrors.RpcErrors400.RingtoneInvalid.ThrowRpcError();
        }

        var converted = await ringtoneConverter.EnsureMp3Async(input.UserId, document);
        var savedId = converted?.Id ?? documentId;

        // The twin supersedes the original: "a new document object that should be used to refer to the
        // ringtone from now on". account.uploadRingtone has already put the original in the list, so without
        // dropping it here the same sound would appear twice — once as the OGG nothing should refer to any
        // more, once as the MP3.
        if (converted != null)
        {
            await savedRingtoneStore.RemoveAsync(input.UserId, documentId);
        }

        var added = await savedRingtoneStore.AddAsync(input.UserId, savedId, limits.MaxSavedCount, documentId);
        if (added)
        {
            await updateNotifier.NotifyAsync(input.UserId, input.PermAuthKeyId);
        }

        // The client must be told to refer to the new document from now on; when nothing was converted the
        // plain constructor is the answer, including for a sound that was already in the list.
        return converted == null
            ? new MyTelegram.Schema.Account.TSavedRingtone()
            : new MyTelegram.Schema.Account.TSavedRingtoneConverted { Document = converted };
    }

    private static bool HasAudioAttribute(IDocumentReadModel document)
    {
        return AudioAttributeOf(document) != null;
    }

    private static int DurationOf(IDocumentReadModel document)
    {
        return AudioAttributeOf(document)?.Duration ?? 0;
    }

    private static MyTelegram.Schema.TDocumentAttributeAudio? AudioAttributeOf(IDocumentReadModel document)
    {
        return document.Attributes2?
            .OfType<MyTelegram.Schema.TDocumentAttributeAudio>()
            .FirstOrDefault();
    }
}
