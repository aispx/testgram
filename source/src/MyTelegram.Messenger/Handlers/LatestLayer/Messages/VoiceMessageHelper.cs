namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Single source of truth for deciding whether a message carries a voice note, used to
/// enforce <c>privacyKeyVoiceMessages</c>.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
/// <remarks>
/// The distinction that matters here is voice note vs. regular music: both are documents
/// carrying a <see cref="TDocumentAttributeAudio"/>, and only the one with the
/// <c>voice</c> flag set is covered by the privacy key. Treating any audio attachment as a
/// voice note would block sending ordinary music to users who merely disallowed voice
/// messages.
/// </remarks>
internal static class VoiceMessageHelper
{
    /// <summary>
    /// Whether the media about to be sent is a voice note.
    /// </summary>
    public static bool IsVoiceMedia(IInputMedia? media)
    {
        return media switch
        {
            TInputMediaUploadedDocument uploaded => HasVoiceAttribute(uploaded.Attributes),
            _ => false
        };
    }

    /// <summary>
    /// Whether any of the media about to be sent in an album is a voice note.
    /// </summary>
    public static bool ContainsVoiceMedia(IEnumerable<IInputSingleMedia>? multiMedia)
    {
        return multiMedia?.Any(p => p is TInputSingleMedia single && IsVoiceMedia(single.Media)) == true;
    }

    /// <summary>
    /// Whether an already stored message carries a voice note. Used on the forward path,
    /// where the media has been persisted rather than supplied by the client.
    /// </summary>
    public static bool IsVoiceMessage(IMessageReadModel message)
    {
        return message.Media2 switch
        {
            TMessageMediaDocument { Voice: true } => true,
            TMessageMediaDocument { Document: TDocument document } => HasVoiceAttribute(document.Attributes),
            _ => false
        };
    }

    private static bool HasVoiceAttribute(IEnumerable<IDocumentAttribute>? attributes)
    {
        return attributes?.Any(p => p is TDocumentAttributeAudio { Voice: true }) == true;
    }
}
