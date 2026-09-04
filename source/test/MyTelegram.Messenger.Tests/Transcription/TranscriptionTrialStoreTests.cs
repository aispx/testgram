using MyTelegram.Messenger.Services.Transcription;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: the free-trial counter behind
/// <a href="https://corefork.telegram.org/api/config#transcribe-audio-trial-weekly-number">transcribe_audio_trial_weekly_number</a>.
///
/// <para>
/// The two numbers this store produces are rendered by every client: Android puts <c>trial_remains_num</c>
/// in its Premium bulletin, tdesktop in <c>ShowTrialTranscribesToast</c>, tdlib in
/// <c>updateSpeechRecognitionTrial</c>. And <c>trial_remains_until_date</c> has to exist from the
/// <i>first</i> call, not appear only once the quota runs out — iOS reads it as the cooldown precisely
/// when the count is 0 and clears its stored cooldown when it is missing.
/// </para>
///
/// <para>
/// Exhaustion is reported as <c>FLOOD_WAIT_%d</c>, so the reset date is also the retry-after the caller is
/// given; a date that drifted would make the client wait the wrong length of time and re-ask early
/// forever.
/// </para>
/// </summary>
public class TranscriptionTrialStoreTests
{
    private const long UserId = 2_010_001;
    private const long OtherUserId = 2_010_002;
    private const int WeeklyNumber = 3;
    private const int WindowDays = 7;

    [RequiresMongoDbFact]
    public async Task Three_tries_are_granted_and_then_the_window_is_exhausted()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        (await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(2);
        (await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(1);
        (await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(0);

        var exhausted = await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        exhausted.Remaining.ShouldBeLessThan(0);
    }

    /// <summary>
    /// The date is what a client displays and what <c>FLOOD_WAIT_%d</c> is computed from, so it must be set
    /// by the first call of a window rather than appearing at the end of it.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_first_try_of_a_window_sets_its_reset_date()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var first = await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        first.ResetDate.ShouldBeGreaterThan(now);
        first.ResetDate.ShouldBeLessThanOrEqualTo(now + WindowDays * 24 * 60 * 60 + 5);

        // And it does not move while the window runs: a client that stored the date must not be told a
        // different one on the next call.
        var second = await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);
        second.ResetDate.ShouldBe(first.ResetDate);
    }

    [RequiresMongoDbFact]
    public async Task An_exhausted_window_still_reports_the_date_to_wait_for()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        var first = await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);
        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);
        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        var exhausted = await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        exhausted.ResetDate.ShouldBe(first.ResetDate);
    }

    /// <summary>
    /// Nobody should lose one of three weekly tries because the recognition provider was down. The worker
    /// refunds on a final failure.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_refund_hands_exactly_one_try_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);
        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        await store.RefundAsync(UserId);

        (await store.GetStateAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(2);
    }

    /// <summary>A refund on an untouched account must not push the counter below zero.</summary>
    [RequiresMongoDbFact]
    public async Task A_refund_with_nothing_spent_changes_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        await store.RefundAsync(UserId);

        (await store.GetStateAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(WeeklyNumber);
    }

    /// <summary>
    /// A window whose date has passed reports a full quota and no cooldown, which is the same conclusion
    /// the clients reach for themselves (tdlib's <c>TrialParameters::update_left_tries</c>).
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_elapsed_window_starts_over()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        // A window one day long, consumed to the end, then read back as though it had already elapsed:
        // the store keys the reset off the stored date, so a zero-length window is the same situation.
        await store.ConsumeAsync(UserId, WeeklyNumber, windowDays: 1);
        await store.ConsumeAsync(UserId, WeeklyNumber, windowDays: 1);
        await store.ConsumeAsync(UserId, WeeklyNumber, windowDays: 1);

        await mongo.Database
            .GetCollection<MongoDB.Bson.BsonDocument>(TranscriptionTrialStore.CollectionName)
            .UpdateOneAsync(
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id", UserId),
                MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Set("ResetDate", 1));

        (await store.GetStateAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(WeeklyNumber);
        (await store.GetStateAsync(UserId, WeeklyNumber, WindowDays)).ResetDate.ShouldBe(0);

        (await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(WeeklyNumber - 1);
    }

    [RequiresMongoDbFact]
    public async Task The_counter_is_per_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);
        await store.ConsumeAsync(UserId, WeeklyNumber, WindowDays);

        (await store.GetStateAsync(OtherUserId, WeeklyNumber, WindowDays)).Remaining.ShouldBe(WeeklyNumber);
    }

    /// <summary>
    /// A deployment that sets the weekly number to 0 has no trial at all, and the first call must be
    /// refused rather than granted.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_weekly_number_of_zero_grants_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new TranscriptionTrialStore(mongo.Database);

        (await store.ConsumeAsync(UserId, weeklyNumber: 0, WindowDays)).Remaining.ShouldBeLessThan(0);
    }
}
