namespace MyTelegram.Messenger.Services.Transcription;

/// <param name="Value">The <c>transcribe_audio_trial_cooldown_until</c> pair to add to the config.</param>
/// <param name="Hash">
/// Mixed into the configuration hash, so a client is not answered <c>appConfigNotModified</c> while
/// holding a cooldown that has since moved.
/// </param>
public sealed record TranscriptionAppConfigEntry(TJsonObjectValue Value, int Hash);

/// <summary>
/// Builds the <c>transcribe_audio_trial_cooldown_until</c> entry of <c>help.getAppConfig</c>: when the
/// caller's free <a href="https://corefork.telegram.org/api/transcribe">transcription</a> quota comes
/// back.
///
/// <para><b>Per account, so it cannot live in the shared configuration.</b> It is the one transcription
/// field whose value differs between callers — <c>transcribe_audio_trial_weekly_number</c> and
/// <c>transcribe_audio_trial_duration_max</c> are the same for everybody and stay in
/// <c>AppConfigHelper.g.cs</c>. Same arrangement as <c>emojies_sounds</c>, for the same reason.</para>
///
/// <para><b>Emitted only while a cooldown is actually pending.</b> Every client defaults the key to 0
/// when it is absent (tdesktop <c>Transcribes::trialsRefreshAt</c>, Android's
/// <c>transcribe_audio_trial_cooldown_until</c> case, tdlib's
/// <c>on_update_trial_parameters</c>, which keeps its stored date when the value is not positive), so
/// leaving it out costs nothing and keeps the configuration hash stable for the common case — a key that
/// changed on every quota movement would break <c>appConfigNotModified</c> for every caller.</para>
/// </summary>
public interface ITranscriptionAppConfigBuilder
{
    Task<TranscriptionAppConfigEntry?> BuildAsync(long userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TranscriptionAppConfigBuilder(
    ITranscriptionLimits limits,
    ITranscriptionTrialStore trialStore,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : ITranscriptionAppConfigBuilder, ITransientDependency
{
    public const string ConfigKey = "transcribe_audio_trial_cooldown_until";

    public async Task<TranscriptionAppConfigEntry?> BuildAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        var weeklyNumber = limits.TrialWeeklyNumber;
        if (weeklyNumber <= 0 || userId == 0)
        {
            return null;
        }

        var state = await trialStore.GetStateAsync(userId, weeklyNumber,
            options.CurrentValue.Transcription.TrialWindowDays, cancellationToken);

        // Only once the quota is actually spent. While tries are left the reset date is not information a
        // client needs, and publishing it would move the hash every week for no reason.
        if (state.Remaining > 0 || state.ResetDate <= 0)
        {
            return null;
        }

        var entry = new TJsonObjectValue
        {
            Key = ConfigKey,
            Value = new TJsonNumber { Value = state.ResetDate }
        };

        return new TranscriptionAppConfigEntry(entry, state.ResetDate);
    }
}
