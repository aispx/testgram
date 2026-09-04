using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>The state of one account's free-trial window.</summary>
/// <param name="Remaining">
/// What goes into <c>messages.transcribedAudio.trial_remains_num</c>. Android stores it as
/// <c>transcribeAudioTrialCurrentNumber</c>, tdesktop as <c>_trialsCount</c>, tdlib as
/// <c>left_tries_</c>, iOS through <c>withUpdatedRemainingCount</c>.
/// </param>
/// <param name="ResetDate">
/// What goes into <c>trial_remains_until_date</c>: unix seconds when <paramref name="Remaining"/>
/// returns to the weekly number. 0 when no window is open.
/// </param>
public sealed record TranscriptionTrialState(int Remaining, int ResetDate);

/// <summary>
/// The per-account free-trial counter behind
/// <a href="https://corefork.telegram.org/api/config#transcribe-audio-trial-weekly-number">transcribe_audio_trial_weekly_number</a>.
///
/// <para><b>A try is spent at request time and handed back if recognition fails.</b> Request time is
/// when the number reaches the client — it is carried by the very response that starts the work, and
/// every client immediately renders it (Android's <c>needShowPremiumBulletin</c>, tdesktop's
/// <c>ShowTrialTranscribesToast</c>). But the work can still fail afterwards, and charging somebody
/// three tries for three provider errors would be indefensible, so a failed transcription refunds.</para>
/// </summary>
public interface ITranscriptionTrialStore
{
    /// <summary>The current state without changing it, for the appConfig cooldown key.</summary>
    Task<TranscriptionTrialState> GetStateAsync(long userId, int weeklyNumber, int windowDays,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends one try. <c>Remaining</c> in the result is what is left <i>after</i> this call, which is
    /// what the response reports; a result whose <c>Remaining</c> is negative means the window is
    /// exhausted and nothing was spent.
    /// </summary>
    Task<TranscriptionTrialState> ConsumeAsync(long userId, int weeklyNumber, int windowDays,
        CancellationToken cancellationToken = default);

    /// <summary>Hands one try back after a failed recognition.</summary>
    Task RefundAsync(long userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TranscriptionTrialStore(IMongoDatabase mongoDatabase) : ITranscriptionTrialStore, ITransientDependency
{
    public const string CollectionName = "transcribe_audio_trials";

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<TranscriptionTrialState> GetStateAsync(long userId, int weeklyNumber, int windowDays,
        CancellationToken cancellationToken = default)
    {
        var row = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync(cancellationToken);

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var used = row == null ? 0 : (int)GetInt64(row, "Used");
        var resetDate = row == null ? 0 : (int)GetInt64(row, "ResetDate");

        if (resetDate != 0 && resetDate <= now)
        {
            // The window is over. Reported as a full quota and no cooldown, which is what the clients
            // compute for themselves anyway (tdlib's TrialParameters::update_left_tries).
            return new TranscriptionTrialState(weeklyNumber, 0);
        }

        return new TranscriptionTrialState(Math.Max(0, weeklyNumber - used), resetDate);
    }

    public async Task<TranscriptionTrialState> ConsumeAsync(long userId, int weeklyNumber, int windowDays,
        CancellationToken cancellationToken = default)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Reset an elapsed window first, so the increment below counts inside the new one. Conditional on
        // the stored date so two concurrent calls cannot both reset and both get a full quota.
        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", userId),
                Builders<BsonDocument>.Filter.Lte("ResetDate", now)),
            Builders<BsonDocument>.Update
                .Set("Used", 0)
                .Set("ResetDate", 0),
            cancellationToken: cancellationToken);

        var row = await Collection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Inc("Used", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

        var used = (int)GetInt64(row, "Used");
        var resetDate = (int)GetInt64(row, "ResetDate");

        if (used > weeklyNumber)
        {
            // Over the limit: undo the increment so a stream of refused calls cannot push the counter
            // arbitrarily far past the quota, and report the cooldown the client should display.
            await Collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", userId),
                Builders<BsonDocument>.Update.Inc("Used", -1),
                cancellationToken: cancellationToken);

            return new TranscriptionTrialState(-1, resetDate == 0 ? now + WindowSeconds(windowDays) : resetDate);
        }

        if (resetDate == 0)
        {
            // The first try of a window sets its end. Every client renders this date, so it has to exist
            // from the first call rather than appearing only once the quota runs out.
            resetDate = now + WindowSeconds(windowDays);

            await Collection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", userId),
                Builders<BsonDocument>.Update.Set("ResetDate", resetDate),
                cancellationToken: cancellationToken);
        }

        return new TranscriptionTrialState(Math.Max(0, weeklyNumber - used), resetDate);
    }

    public Task RefundAsync(long userId, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", userId),
                Builders<BsonDocument>.Filter.Gt("Used", 0)),
            Builders<BsonDocument>.Update.Inc("Used", -1),
            cancellationToken: cancellationToken);
    }

    private static int WindowSeconds(int windowDays)
    {
        return Math.Max(1, windowDays) * 24 * 60 * 60;
    }

    private static long GetInt64(BsonDocument row, string name)
    {
        if (!row.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
