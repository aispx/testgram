using MyTelegram.Messenger.Handlers.LatestLayer.Messages;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>What a stored message offers to speech recognition.</summary>
/// <param name="DocumentId">
/// The document whose body is recognised, and the key of the shared text cache — the same voice note
/// forwarded into ten chats is one recognition, not ten.
/// </param>
/// <param name="DurationSeconds">
/// Rounded to the nearest second, which is the number compared against the advertised
/// <c>transcribe_audio_trial_duration_max</c>. 0 when the attribute carries no duration.
/// </param>
/// <param name="MimeType">Declared mime type, used to pick the temp file extension for ffmpeg.</param>
/// <param name="IsRoundVideo">A video note rather than a voice note; both are transcribable.</param>
public sealed record TranscribableMedia(long DocumentId, int DurationSeconds, string? MimeType, bool IsRoundVideo);

/// <summary>
/// Decides whether a stored message can be transcribed at all, and pulls out what recognition needs.
///
/// <para><b>Round video notes count, ordinary music does not.</b> tdlib's
/// <c>can_recognize_message_speech</c> accepts exactly <c>VideoNote</c> and <c>VoiceNote</c>; Android
/// offers the button for <c>isVoice() || isRoundVideo()</c> (<c>MessageObject</c>) and tdesktop marks the
/// entry <c>roundview</c> when <c>document->isVideoMessage()</c>. A plain audio file carries a
/// <c>documentAttributeAudio</c> too, so treating any audio attachment as transcribable would spend the
/// caller's trial on a music track no client will render the result for.</para>
/// See https://corefork.telegram.org/api/transcribe
/// </summary>
internal static class TranscribableMediaResolver
{
    /// <summary>
    /// The recognisable media of <paramref name="message"/>, or null when it carries none. The voice
    /// half of the decision is delegated to <see cref="VoiceMessageHelper.IsVoiceMessage"/> so there is
    /// one definition of "voice note" in the repository; the duration is then read off the attribute,
    /// which that helper does not expose.
    /// </summary>
    public static TranscribableMedia? Resolve(IMessageReadModel message)
    {
        if (message.Media2 is not TMessageMediaDocument media)
        {
            return null;
        }

        if (media.Document is not TDocument { Id: not 0 } document)
        {
            return null;
        }

        var attributes = document.Attributes;

        if (VoiceMessageHelper.IsVoiceMessage(message))
        {
            var audio = attributes?.OfType<TDocumentAttributeAudio>().FirstOrDefault(p => p.Voice);

            return new TranscribableMedia(document.Id, audio?.Duration ?? 0, document.MimeType, false);
        }

        var video = attributes?.OfType<TDocumentAttributeVideo>().FirstOrDefault(p => p.RoundMessage);
        if (video != null || media.Round)
        {
            var duration = video == null
                ? 0
                : (int)Math.Round(video.Duration, MidpointRounding.AwayFromZero);

            return new TranscribableMedia(document.Id, duration, document.MimeType, true);
        }

        return null;
    }
}
