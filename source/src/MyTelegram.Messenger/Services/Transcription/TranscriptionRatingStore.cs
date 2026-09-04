using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>
/// Records what <c>messages.rateTranscribedAudio</c> was told about a transcription.
///
/// <para>Nothing reads it back over the wire — clients remember for themselves whether they have rated
/// (tdesktop <c>markTranscriptionAsRated</c>, iOS <c>withDidRate()</c>), and there is no method that
/// returns a rating. It is stored because it is the only feedback signal this surface produces: which
/// model and which audio produced a transcript somebody marked wrong is exactly what a later change to
/// <c>App__Transcription__Model</c> should be argued from.</para>
/// See https://corefork.telegram.org/method/messages.rateTranscribedAudio
/// </summary>
public interface ITranscriptionRatingStore
{
    /// <summary>
    /// Idempotent: rating the same transcription twice overwrites rather than accumulating, which is what
    /// a client that lost its "already rated" flag will do.
    /// </summary>
    Task SaveAsync(long userId, long transcriptionId, long documentId, bool good,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TranscriptionRatingStore(IMongoDatabase mongoDatabase)
    : ITranscriptionRatingStore, ITransientDependency
{
    public const string CollectionName = "transcription_ratings";

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public Task SaveAsync(long userId, long transcriptionId, long documentId, bool good,
        CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"{userId}:{transcriptionId}"),
            Builders<BsonDocument>.Update
                .Set("UserId", userId)
                .Set("TranscriptionId", transcriptionId)
                .Set("DocumentId", documentId)
                .Set("Good", good)
                .Set("Date", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
}
