namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>
/// The three numbers that bound <a href="https://corefork.telegram.org/api/transcribe">voice message
/// transcription</a>, from <a href="https://corefork.telegram.org/api/config">appConfig</a>.
///
/// <para>They have to be read from the same place the client was told, because the client renders them:
/// tdesktop shows the remaining count and the reset date in its trial toast
/// (<c>ShowTrialTranscribesToast</c>), Android formats its Premium bulletin from
/// <c>transcribeAudioTrialCurrentNumber</c>, and both decide whether the trial exists at all from
/// <c>transcribe_audio_trial_weekly_number</c>. Refusing at a different number than the advertised one
/// produces a message that contradicts itself.</para>
/// </summary>
public interface ITranscriptionLimits
{
    /// <summary><c>transcribe_audio_trial_weekly_number</c> — free transcriptions per week.</summary>
    int TrialWeeklyNumber { get; }

    /// <summary><c>transcribe_audio_trial_duration_max</c> — seconds, non-Premium only.</summary>
    int TrialDurationMaxSeconds { get; }

    /// <summary>
    /// <c>group_transcribe_level_min</c> — the boost level at which a supergroup transcribes free for
    /// its non-Premium members. tdesktop's <c>Transcribes::freeFor()</c> compares against exactly this.
    /// </summary>
    int GroupFreeLevelMin { get; }
}

/// <inheritdoc />
public class TranscriptionLimits(IAppConfigHelper appConfigHelper) : ITranscriptionLimits, ITransientDependency
{
    /// <summary>Fallbacks match what <c>AppConfigHelper</c> emits.</summary>
    public const int WeeklyNumberFallback = 3;

    public const int TrialDurationFallback = 300;
    public const int GroupFreeLevelFallback = 6;

    public int TrialWeeklyNumber =>
        appConfigHelper.GetInt32Value("transcribe_audio_trial_weekly_number", WeeklyNumberFallback);

    public int TrialDurationMaxSeconds =>
        appConfigHelper.GetInt32Value("transcribe_audio_trial_duration_max", TrialDurationFallback);

    public int GroupFreeLevelMin =>
        appConfigHelper.GetInt32Value("group_transcribe_level_min", GroupFreeLevelFallback);
}
