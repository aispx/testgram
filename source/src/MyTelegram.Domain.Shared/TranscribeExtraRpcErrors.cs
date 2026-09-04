namespace MyTelegram;

/// <summary>
/// Hand-written RPC errors for <a href="https://corefork.telegram.org/api/transcribe">voice message
/// transcription »</a> that are not present in the generated <see cref="RpcErrors"/>
/// (<c>RpcErrors.g.cs</c>). Do not add these to the generated file; it is regenerated and would lose
/// manual edits.
/// </summary>
public static class TranscribeExtraRpcErrors
{
    /// <summary>
    /// The voice message is longer than this account may have transcribed — for a non-Premium caller
    /// <a href="https://corefork.telegram.org/api/config#transcribe-audio-trial-duration-max">transcribe_audio_trial_duration_max</a>,
    /// otherwise <c>App__Transcription__MaxDurationSeconds</c> or the recognition provider's own cap.
    ///
    /// <para><c>messages.transcribeAudio</c> does document this error, but the generated list does not
    /// carry it. Both tdesktop (<c>Transcribes::load</c>, which sets <c>entry.toolong</c>) and iOS
    /// (<c>_internal_transcribeAudio</c>, which maps it to <c>.tooLong</c>) compare the string
    /// literally, so the spelling is the contract — anything else renders as a generic failure.</para>
    /// <code>
    /// messages.transcribeAudio
    /// </code>
    /// </summary>
    public static readonly RpcError MsgVoiceTooLong = new(400, "MSG_VOICE_TOO_LONG");
}
