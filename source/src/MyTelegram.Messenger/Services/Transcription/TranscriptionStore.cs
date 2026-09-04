using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Transcription;

/// <inheritdoc />
public class TranscriptionStore(IMongoDatabase mongoDatabase, ILogger<TranscriptionStore> logger)
    : ITranscriptionStore, ITransientDependency
{
    public const string CollectionName = "transcriptions";
    public const string TextCollectionName = "transcription_texts";

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    private IMongoCollection<BsonDocument> TextCollection =>
        mongoDatabase.GetCollection<BsonDocument>(TextCollectionName);

    public async Task<TranscriptionDocument?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var row = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? null : Map(row);
    }

    public async Task<TranscriptionDocument> EnqueueAsync(TranscriptionDocument document,
        CancellationToken cancellationToken = default)
    {
        document.Pending = true;
        document.Failed = false;

        try
        {
            await Collection.InsertOneAsync(ToBson(document), cancellationToken: cancellationToken);

            return document;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await GetAsync(document.Id, cancellationToken);

            if (existing == null)
            {
                throw;
            }

            // A row that failed is not an answer. Tapping the button again is how a user retries, so the
            // failed row is replaced by this fresh one - returning it would hand back an empty final
            // transcription that nothing will ever fill in.
            if (existing.Failed)
            {
                await Collection.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", document.Id),
                    ToBson(document),
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken);

                return document;
            }

            // Two devices tapped the same message at the same time. The row that won owns the
            // transcription_id, and both clients have to be told the same one.
            return existing;
        }
    }

    public async Task<TranscriptionDocument> SaveCompletedAsync(TranscriptionDocument document,
        CancellationToken cancellationToken = default)
    {
        document.Pending = false;
        document.Failed = false;

        await Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", document.Id),
            ToBson(document),
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

        return document;
    }

    public async Task<List<TranscriptionDocument>> ClaimAsync(int max, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claimedUntil = now + leaseSeconds;
        var claimed = new List<TranscriptionDocument>(max);

        for (var i = 0; i < max; i++)
        {
            var row = await Collection.FindOneAndUpdateAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("Pending", true),
                    Builders<BsonDocument>.Filter.Ne("Failed", true),
                    Builders<BsonDocument>.Filter.Lt("ClaimedUntil", now)),
                Builders<BsonDocument>.Update
                    .Set("ClaimedUntil", claimedUntil)
                    .Inc("Attempts", 1),
                new FindOneAndUpdateOptions<BsonDocument>
                {
                    ReturnDocument = ReturnDocument.After,
                    Sort = Builders<BsonDocument>.Sort.Ascending("Date")
                },
                cancellationToken);

            if (row == null)
            {
                break;
            }

            claimed.Add(Map(row));
        }

        return claimed;
    }

    public Task CompleteAsync(string id, string text, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("Text", text)
                .Set("Pending", false)
                .Set("Failed", false)
                .Set("ClaimedUntil", 0),
            cancellationToken: cancellationToken);
    }

    public Task FailAsync(string id, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("Pending", false)
                .Set("Failed", true)
                .Set("ClaimedUntil", 0),
            cancellationToken: cancellationToken);
    }

    public Task ReleaseAsync(string id, int attempts, int nextAttemptDate,
        CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update
                .Set("Attempts", attempts)
                .Set("ClaimedUntil", nextAttemptDate),
            cancellationToken: cancellationToken);
    }

    public async Task<string?> GetCachedTextAsync(long documentId, CancellationToken cancellationToken = default)
    {
        var row = await TextCollection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", documentId))
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null || !row.TryGetValue("Text", out var value) || value.BsonType != BsonType.String)
        {
            return null;
        }

        return value.AsString;
    }

    public async Task SaveCachedTextAsync(long documentId, string text, string? language,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<BsonDocument>.Update
            .Set("Text", text)
            .Set("Date", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (!string.IsNullOrWhiteSpace(language))
        {
            update = update.Set("Language", language);
        }

        await TextCollection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", documentId),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// The index the claim loop scans. <c>MongoDbIndexesCreator</c> only covers the read models EventFlow
    /// projects, and this is a plain collection, so it is created here — the same place
    /// <c>saved_ringtones</c> and the other hand-written collections do it.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("Pending")
                        .Ascending("ClaimedUntil")
                        .Ascending("Date"),
                    new CreateIndexOptions { Name = "idx_transcription_queue" }),
                cancellationToken: cancellationToken);

            await Collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TranscriptionId"),
                    new CreateIndexOptions { Name = "idx_transcription_id" }),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // A missing index is a slow claim, not a broken one.
            logger.LogWarning(ex, "The transcription indexes could not be created");
        }
    }

    private static BsonDocument ToBson(TranscriptionDocument document)
    {
        return new BsonDocument
        {
            { "_id", document.Id },
            { "OwnerPeerId", document.OwnerPeerId },
            { "MsgId", document.MsgId },
            { "PeerId", document.PeerId },
            { "PeerType", (int)document.PeerType },
            { "RequestedByUserId", document.RequestedByUserId },
            { "DocumentId", document.DocumentId },
            { "MimeType", document.MimeType ?? (BsonValue)BsonNull.Value },
            { "TranscriptionId", document.TranscriptionId },
            { "Text", document.Text },
            { "Pending", document.Pending },
            { "Failed", document.Failed },
            { "Attempts", document.Attempts },
            { "ClaimedUntil", document.ClaimedUntil },
            { "TrialConsumed", document.TrialConsumed },
            { "Date", document.Date }
        };
    }

    private static TranscriptionDocument Map(BsonDocument row)
    {
        return new TranscriptionDocument
        {
            Id = row["_id"].AsString,
            OwnerPeerId = GetInt64(row, "OwnerPeerId"),
            MsgId = (int)GetInt64(row, "MsgId"),
            PeerId = GetInt64(row, "PeerId"),
            PeerType = (PeerType)(int)GetInt64(row, "PeerType"),
            RequestedByUserId = GetInt64(row, "RequestedByUserId"),
            DocumentId = GetInt64(row, "DocumentId"),
            MimeType = row.TryGetValue("MimeType", out var mimeType) && mimeType.BsonType == BsonType.String
                ? mimeType.AsString
                : null,
            TranscriptionId = GetInt64(row, "TranscriptionId"),
            Text = row.TryGetValue("Text", out var text) && text.BsonType == BsonType.String
                ? text.AsString
                : string.Empty,
            Pending = GetBool(row, "Pending"),
            Failed = GetBool(row, "Failed"),
            Attempts = (int)GetInt64(row, "Attempts"),
            ClaimedUntil = (int)GetInt64(row, "ClaimedUntil"),
            TrialConsumed = GetBool(row, "TrialConsumed"),
            Date = (int)GetInt64(row, "Date")
        };
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

    private static bool GetBool(BsonDocument row, string name)
    {
        return row.TryGetValue(name, out var value) && value.BsonType == BsonType.Boolean && value.AsBoolean;
    }
}
