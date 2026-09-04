using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Transcription;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: who may transcribe, and what it costs them.
///
/// <para>
/// The order of the checks is the whole point. A voice note the server was always going to refuse as too
/// long must not cost one of three weekly tries, and the ceiling differs per path — the advertised
/// <c>transcribe_audio_trial_duration_max</c> for a trial call, <c>App__Transcription__MaxDurationSeconds</c>
/// for a Premium one — so the duration cannot be checked by the caller after the fact.
/// </para>
///
/// <para>
/// The two trial fields also have to move together: they share flag bit 1 of
/// <c>messages.transcribedAudio</c>, and an unlimited caller handed <c>trial_remains_num = 0</c> is exactly
/// the state Android renders as an exhausted quota.
/// </para>
/// </summary>
public class TranscriptionEligibilityTests
{
    private const long PremiumUserId = 2_010_001;
    private const long PlainUserId = 2_010_002;
    private const long ChannelId = 1_500_000_001;

    private static readonly Peer PrivatePeer = new(PeerType.User, 2_010_003);
    private static readonly Peer ChannelPeer = new(PeerType.Channel, ChannelId);

    [RequiresMongoDbFact]
    public async Task A_premium_caller_is_unlimited_and_carries_no_trial_fields()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo);

        var allowance = await service.EvaluateAsync(PremiumUserId, PrivatePeer, durationSeconds: 30);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.Unlimited);
        allowance.Remaining.ShouldBeNull();
        allowance.ResetDate.ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task A_plain_caller_spends_a_try_and_is_told_both_numbers()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo);

        var allowance = await service.EvaluateAsync(PlainUserId, PrivatePeer, durationSeconds: 30);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.Trial);
        allowance.Remaining.ShouldBe(2);
        allowance.ResetDate.ShouldNotBeNull();
        allowance.ResetDate!.Value.ShouldBeGreaterThan((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// The regression this test exists for: refusing after consuming charged a try for a message that was
    /// never going to be transcribed, and three of those left the account waiting a week for nothing.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_note_over_the_trial_duration_is_refused_without_spending_a_try()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var trialStore = new TranscriptionTrialStore(mongo.Database);
        var service = Service(mongo, trialStore);

        var allowance = await service.EvaluateAsync(PlainUserId, PrivatePeer,
            durationSeconds: TranscriptionLimits.TrialDurationFallback + 1);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.TooLong);

        (await trialStore.GetStateAsync(PlainUserId, TranscriptionLimits.WeeklyNumberFallback, 7))
            .Remaining.ShouldBe(TranscriptionLimits.WeeklyNumberFallback);
    }

    /// <summary>A Premium caller has no duration ceiling unless the deployment sets one.</summary>
    [RequiresMongoDbFact]
    public async Task A_premium_caller_has_no_duration_ceiling_by_default()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo);

        var allowance = await service.EvaluateAsync(PremiumUserId, PrivatePeer, durationSeconds: 10_000);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.Unlimited);
    }

    [RequiresMongoDbFact]
    public async Task A_configured_premium_ceiling_is_enforced()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo, maxDurationSeconds: 600);

        (await service.EvaluateAsync(PremiumUserId, PrivatePeer, durationSeconds: 601))
            .Allowance.ShouldBe(TranscriptionAllowance.TooLong);

        (await service.EvaluateAsync(PremiumUserId, PrivatePeer, durationSeconds: 600))
            .Allowance.ShouldBe(TranscriptionAllowance.Unlimited);
    }

    /// <summary>
    /// The fourth call reports the wait rather than <c>PREMIUM_ACCOUNT_REQUIRED</c>: every client reads
    /// the retry-after out of <c>FLOOD_WAIT_%d</c> and turns it into the cooldown it displays.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_exhausted_quota_reports_a_wait_rather_than_a_premium_requirement()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo);

        for (var i = 0; i < TranscriptionLimits.WeeklyNumberFallback; i++)
        {
            await service.EvaluateAsync(PlainUserId, PrivatePeer, durationSeconds: 30);
        }

        var allowance = await service.EvaluateAsync(PlainUserId, PrivatePeer, durationSeconds: 30);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.Exhausted);
        allowance.RetryAfterSeconds.ShouldBeGreaterThan(0);
        allowance.Remaining.ShouldBe(0);
        allowance.ResetDate.ShouldNotBeNull();
    }

    /// <summary>
    /// A supergroup boosted to <c>group_transcribe_level_min</c> pays for its members' transcriptions,
    /// which is what tdesktop's <c>Transcribes::freeFor()</c> compares against.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_boosted_supergroup_transcribes_free_for_a_plain_member()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var trialStore = new TranscriptionTrialStore(mongo.Database);
        var service = Service(mongo, trialStore, boostLevel: TranscriptionLimits.GroupFreeLevelFallback);

        var allowance = await service.EvaluateAsync(PlainUserId, ChannelPeer, durationSeconds: 30);

        allowance.Allowance.ShouldBe(TranscriptionAllowance.Unlimited);
        allowance.Remaining.ShouldBeNull();

        (await trialStore.GetStateAsync(PlainUserId, TranscriptionLimits.WeeklyNumberFallback, 7))
            .Remaining.ShouldBe(TranscriptionLimits.WeeklyNumberFallback);
    }

    [RequiresMongoDbFact]
    public async Task An_unboosted_supergroup_draws_on_the_trial()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo, boostLevel: TranscriptionLimits.GroupFreeLevelFallback - 1);

        (await service.EvaluateAsync(PlainUserId, ChannelPeer, durationSeconds: 30))
            .Allowance.ShouldBe(TranscriptionAllowance.Trial);
    }

    /// <summary>
    /// A broadcast channel has no equivalent of the supergroup exemption, so its boost level must not
    /// grant one.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_broadcast_channel_does_not_pay_for_transcriptions()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = Service(mongo, boostLevel: 100, megaGroup: false);

        (await service.EvaluateAsync(PlainUserId, ChannelPeer, durationSeconds: 30))
            .Allowance.ShouldBe(TranscriptionAllowance.Trial);
    }

    private static TranscriptionEligibilityService Service(EmbeddedMongoServer mongo,
        ITranscriptionTrialStore? trialStore = null, int boostLevel = 0, bool megaGroup = true,
        int maxDurationSeconds = 0)
    {
        var users = new Mock<IUserAppService>(MockBehavior.Loose);
        users.Setup(p => p.GetAsync(It.IsAny<long?>()))
            .Returns((long? id) => Task.FromResult(User(id ?? 0)));

        var channels = new Mock<IChannelAppService>(MockBehavior.Loose);
        channels.Setup(p => p.GetAsync(It.IsAny<long?>()))
            .Returns(() => Task.FromResult(Channel(megaGroup)));

        var boosts = new Mock<IBoostLevelCalculator>(MockBehavior.Loose);
        boosts.Setup(p => p.GetLevelAsync(It.IsAny<long>())).ReturnsAsync(boostLevel);

        var options = new MyTelegramMessengerServerOptions
        {
            Transcription = new TranscriptionConfig
            {
                TrialWindowDays = 7,
                MaxDurationSeconds = maxDurationSeconds
            }
        };

        return new TranscriptionEligibilityService(
            users.Object,
            channels.Object,
            boosts.Object,
            new FixedTranscriptionLimits(),
            trialStore ?? new TranscriptionTrialStore(mongo.Database),
            new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(options),
            NullLogger<TranscriptionEligibilityService>.Instance);
    }

    private static IUserReadModel? User(long userId)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(userId);
        user.SetupGet(p => p.Premium).Returns(userId == PremiumUserId);

        return user.Object;
    }

    private static IChannelReadModel? Channel(bool megaGroup)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channel.SetupGet(p => p.MegaGroup).Returns(megaGroup);
        channel.SetupGet(p => p.Broadcast).Returns(!megaGroup);

        return channel.Object;
    }

    /// <summary>
    /// The advertised numbers, without going through <c>AppConfigHelper</c>: the point of these tests is the
    /// decision, and <c>TranscribedAudioResponseTests</c> already pins that the table agrees with these.
    /// </summary>
    private sealed class FixedTranscriptionLimits : ITranscriptionLimits
    {
        public int TrialWeeklyNumber => TranscriptionLimits.WeeklyNumberFallback;

        public int TrialDurationMaxSeconds => TranscriptionLimits.TrialDurationFallback;

        public int GroupFreeLevelMin => TranscriptionLimits.GroupFreeLevelFallback;
    }
}
