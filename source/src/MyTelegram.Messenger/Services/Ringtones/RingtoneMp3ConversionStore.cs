using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// Remembers the MP3 the server produced from a sound that was not MP3.
///
/// <para>Two things need it. A second <c>account.saveRingtone</c> for the same source must hand back the
/// same twin instead of converting again and leaving two rows for one sound — tdlib calls the method
/// after every upload, and a client whose cache was cleared may re-save a voice message it already
/// saved. And a client that still holds the original id (it saw <c>account.savedRingtone</c> before the
/// conversion existed, or simply kept the message's document) must be able to unsave by that id.</para>
/// Same shape as <c>gif_mp4_conversions</c>.
/// </summary>
public interface IRingtoneMp3ConversionStore
{
    /// <summary>The MP3 twin of <paramref name="documentId"/>, or null when there is none.</summary>
    Task<long?> GetMp3DocumentIdAsync(long documentId, CancellationToken cancellationToken = default);

    Task SaveAsync(long documentId, long mp3DocumentId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class RingtoneMp3ConversionStore(IMongoDatabase mongoDatabase)
    : IRingtoneMp3ConversionStore, ITransientDependency
{
    public const string CollectionName = "ringtone_mp3_conversions";

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<long?> GetMp3DocumentIdAsync(long documentId, CancellationToken cancellationToken = default)
    {
        var row = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", documentId))
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null || !row.TryGetValue("Mp3DocumentId", out var value))
        {
            return null;
        }

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            _ => null
        };
    }

    public Task SaveAsync(long documentId, long mp3DocumentId, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", documentId),
            Builders<BsonDocument>.Update
                .Set("Mp3DocumentId", mp3DocumentId)
                .Set("Date", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
}
