namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>How a caller is allowed to transcribe this message.</summary>
public enum TranscriptionAllowance
{
    /// <summary>Premium, or a supergroup boosted past <c>group_transcribe_level_min</c>.</summary>
    Unlimited,

    /// <summary>A free-trial try was spent.</summary>
    Trial,

    /// <summary>The weekly trial is used up; the caller must be told to wait.</summary>
    Exhausted,

    /// <summary>There is no trial at all and the caller is not Premium.</summary>
    PremiumRequired,

    /// <summary>The media is longer than this caller may have transcribed. Nothing was spent.</summary>
    TooLong
}

/// <param name="Allowance">Which of the paths applies.</param>
/// <param name="Remaining">
/// <c>trial_remains_num</c>, or null when the response must not carry the trial fields at all.
/// </param>
/// <param name="ResetDate">
/// <c>trial_remains_until_date</c>. Set whenever <paramref name="Remaining"/> is, because both share
/// flag bit 1 of <c>messages.transcribedAudio</c> — one without the other cannot be serialized.
/// </param>
/// <param name="RetryAfterSeconds">Seconds to report in <c>FLOOD_WAIT_%d</c> when exhausted.</param>
public sealed record TranscriptionEligibility(
    TranscriptionAllowance Allowance,
    int? Remaining,
    int? ResetDate,
    int RetryAfterSeconds);

/// <summary>
/// Decides whether a caller may transcribe, and spends a free-trial try when that is the path taken.
///
/// <para>Three ways in. Premium is unlimited. A <b>supergroup boosted to
/// <c>group_transcribe_level_min</c> transcribes free for its non-Premium members</b> — that is what
/// tdesktop's <c>Transcribes::freeFor()</c> compares against, and the appConfig field exists for no other
/// purpose. Everybody else draws on the weekly trial.</para>
///
/// <para><b>Exhaustion is <c>FLOOD_WAIT_%d</c>, not <c>PREMIUM_ACCOUNT_REQUIRED</c>.</b> All three client
/// families read the retry-after out of that error and turn it into the cooldown they display: Android
/// (<c>TranscribeButton</c>) sets <c>transcribeAudioTrialCurrentNumber = 0</c> and
/// <c>cooldownUntil = now + X</c>, tdlib does the same in <c>on_transcribed_audio</c> via
/// <c>Global::get_retry_after</c>, and iOS maps it to <c>limitExceeded</c>. Non-Premium Android even sends
/// <c>RequestFlagDoNotWaitFloodWait</c> so the error reaches the UI instead of being retried. Answering
/// <c>PREMIUM_ACCOUNT_REQUIRED</c> there leaves the counter and the cooldown untouched, so the client
/// keeps offering a button that cannot work.</para>
///
/// <para><b>The duration is checked before anything is spent.</b> The ceiling differs per path — the
/// advertised <c>transcribe_audio_trial_duration_max</c> for a trial call, <c>MaxDurationSeconds</c>
/// otherwise — so it has to be decided here rather than by the caller, or a message the server was always
/// going to refuse would still cost one of three weekly tries.</para>
/// See https://corefork.telegram.org/api/transcribe
/// </summary>
public interface ITranscriptionEligibility
{
    Task<TranscriptionEligibility> EvaluateAsync(long userId, Peer peer, int durationSeconds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TranscriptionEligibilityService(
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IBoostLevelCalculator boostLevelCalculator,
    ITranscriptionLimits limits,
    ITranscriptionTrialStore trialStore,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TranscriptionEligibilityService> logger)
    : ITranscriptionEligibility, ITransientDependency
{
    public async Task<TranscriptionEligibility> EvaluateAsync(long userId, Peer peer, int durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var config = options.CurrentValue.Transcription;

        var user = await userAppService.GetAsync((long?)userId);
        if (user?.Premium == true || await IsFreeForGroupAsync(peer))
        {
            return Exceeds(durationSeconds, config.MaxDurationSeconds)
                ? new TranscriptionEligibility(TranscriptionAllowance.TooLong, null, null, 0)
                : new TranscriptionEligibility(TranscriptionAllowance.Unlimited, null, null, 0);
        }

        var weeklyNumber = limits.TrialWeeklyNumber;
        if (weeklyNumber <= 0)
        {
            return new TranscriptionEligibility(TranscriptionAllowance.PremiumRequired, null, null, 0);
        }

        // Before the counter is touched: this is the number the client was told, and refusing after
        // spending a try would charge for a message that was never going to be transcribed.
        if (Exceeds(durationSeconds, limits.TrialDurationMaxSeconds))
        {
            return new TranscriptionEligibility(TranscriptionAllowance.TooLong, null, null, 0);
        }

        var state = await trialStore.ConsumeAsync(userId, weeklyNumber, config.TrialWindowDays,
            cancellationToken);

        if (state.Remaining < 0)
        {
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var retryAfter = Math.Max(1, state.ResetDate - now);

            return new TranscriptionEligibility(TranscriptionAllowance.Exhausted, 0, state.ResetDate, retryAfter);
        }

        // Both fields or neither: they share flag bit 1, and iOS reads trial_remains_until_date as the
        // cooldown precisely when trial_remains_num is 0 - omitting it there *clears* the cooldown it
        // had stored.
        return new TranscriptionEligibility(TranscriptionAllowance.Trial, state.Remaining, state.ResetDate, 0);
    }

    /// <summary>
    /// A ceiling of 0 means there is none; a duration of 0 means the stored media carried no duration
    /// attribute, and inventing a limit for it would refuse a message for a number nobody measured.
    /// </summary>
    private static bool Exceeds(int durationSeconds, int ceilingSeconds)
    {
        return ceilingSeconds > 0 && durationSeconds > ceilingSeconds;
    }

    /// <summary>
    /// Whether the chat itself pays for the transcription. Only supergroups qualify — a broadcast channel
    /// has no equivalent field, and there is nothing to check for a private chat.
    /// </summary>
    private async Task<bool> IsFreeForGroupAsync(Peer peer)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return false;
        }

        var minLevel = limits.GroupFreeLevelMin;
        if (minLevel <= 0)
        {
            return false;
        }

        var channel = await channelAppService.GetAsync((long?)peer.PeerId);
        if (channel is not { MegaGroup: true })
        {
            return false;
        }

        var level = await boostLevelCalculator.GetLevelAsync(peer.PeerId);
        if (level < minLevel)
        {
            return false;
        }

        logger.LogDebug(
            "Transcription in supergroup {ChannelId} is free: boost level {Level} reaches group_transcribe_level_min {MinLevel}",
            peer.PeerId, level, minLevel);

        return true;
    }
}
