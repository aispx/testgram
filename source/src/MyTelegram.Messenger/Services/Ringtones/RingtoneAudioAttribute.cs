namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// Puts the probed <c>documentAttributeAudio</c> on a notification sound that is served without one.
///
/// <para>The document row belongs to the file server, which writes it from the attributes the upload
/// carried. On a deployment where <c>upload.saveFilePart</c> does not stage the parts in this repository's
/// <c>file_parts</c> — the file server keeps its own copy of an upload — the duration is only knowable
/// <i>after</i> the row exists, so it lives in <c>saved_ringtones</c> and is merged in here. Editing the
/// row instead would be undone by the owning aggregate's next event.</para>
///
/// <para>Without the attribute a client shows no length for the tone, and Telegram Android's
/// <c>saveToRingtones</c> has nothing to compare against <c>ringtone_duration_max</c>.</para>
/// </summary>
public static class RingtoneAudioAttribute
{
    /// <summary>
    /// Adds the attribute when <paramref name="document"/> has none and a duration is known. A document that
    /// already carries one is left alone: that one came from the file server and describes the real body.
    /// </summary>
    public static TDocument Merge(TDocument document, int durationSeconds, string? title, string? performer)
    {
        if (durationSeconds <= 0)
        {
            return document;
        }

        document.Attributes ??= new TVector<IDocumentAttribute>();

        if (document.Attributes.OfType<TDocumentAttributeAudio>().Any())
        {
            return document;
        }

        document.Attributes.Add(new TDocumentAttributeAudio
        {
            // Not a voice note: a notification sound is played by the client's own tone player, and the voice
            // flag would file it under voice messages in every UI that groups audio.
            Voice = false,
            Duration = durationSeconds,
            Title = title,
            Performer = performer
        });

        return document;
    }

    /// <inheritdoc cref="Merge(TDocument,int,string?,string?)" />
    public static TDocument Merge(TDocument document, SavedRingtoneDocument row)
    {
        return Merge(document, row.DurationSeconds, row.Title, row.Performer);
    }
}
