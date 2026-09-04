using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// One entry of a user's <a href="https://corefork.telegram.org/api/ringtones">saved notification
/// sounds</a> list.
/// </summary>
[BsonIgnoreExtraElements]
public class SavedRingtoneDocument
{
    /// <summary><c>{UserId}:{DocumentId}</c> — one row per user and sound, so a re-save is an upsert.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }

    /// <summary>The document the list serves, which for a converted sound is the MP3 twin.</summary>
    public long DocumentId { get; set; }

    /// <summary>
    /// The document the client passed to <c>account.saveRingtone</c> when it differs from
    /// <see cref="DocumentId"/>, so an unsave quoting the original still finds the row. 0 when the sound
    /// was saved as it arrived.
    /// </summary>
    public long OriginalDocumentId { get; set; }

    /// <summary>
    /// Strictly increasing per user; the list is served in descending <c>Order</c>, so the newest sound
    /// is first — which is where Telegram iOS (<c>[item] + sounds</c>) and tdesktop
    /// (<c>_list.documents.insert(begin, …)</c>) put a freshly uploaded one locally. Clients hash nothing
    /// themselves here, but they render the vector as received, so the order is still a contract.
    /// </summary>
    public long Order { get; set; }

    public int Date { get; set; }

    /// <summary>
    /// What the sound sounds like, probed with ffprobe when it was uploaded.
    ///
    /// <para>It is kept here rather than in the document row because the row belongs to the file server:
    /// <c>SaveMedia</c> writes it from the attributes the upload carried, and the parts a client staged are
    /// not readable from this repository on every deployment, so the duration is often only known after the
    /// document already exists. <c>account.uploadRingtone</c> and <c>account.getSavedRingtones</c> both build
    /// the TL <c>document</c> here, so merging the attribute in on the way out is what the client sees —
    /// editing a row an aggregate owns would be overwritten by its next event.</para>
    /// </summary>
    public int DurationSeconds { get; set; }

    public string? Title { get; set; }

    public string? Performer { get; set; }

    public static string MakeId(long userId, long documentId) => $"{userId}:{documentId}";
}

/// <summary>
/// Storage behind <c>account.getSavedRingtones</c> / <c>account.saveRingtone</c> /
/// <c>account.uploadRingtone</c>.
/// See https://corefork.telegram.org/api/ringtones
/// </summary>
public interface ISavedRingtoneStore
{
    /// <summary>The user's saved sound ids, newest first, capped at <paramref name="limit"/>.</summary>
    Task<List<long>> GetOrderedIdsAsync(long userId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same list as rows, so the audio attribute probed at upload time can be merged into the documents
    /// the list serves.
    /// </summary>
    Task<List<SavedRingtoneDocument>> GetOrderedAsync(long userId, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the sound at the front of the list and evicts the oldest entries past
    /// <paramref name="limit"/> (<c>ringtone_saved_count_max</c>). Returns false when the sound was
    /// already saved, in which case nothing moves: <c>account.saveRingtone</c> is called again by tdlib
    /// after every upload, and reordering on that would change a hash the client has already stored.
    /// </summary>
    Task<bool> AddAsync(long userId, long documentId, int limit, long originalDocumentId = 0,
        RingtoneAudioInfo? info = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the sound; returns whether a row was actually deleted.</summary>
    Task<bool> RemoveAsync(long userId, long documentId, CancellationToken cancellationToken = default);

    /// <summary>Drops rows whose document no longer exists, so what we store cannot disagree with what we serve.</summary>
    Task RemoveManyAsync(long userId, IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default);

    /// <summary>The row keyed by either the served document or the one the client originally saved.</summary>
    Task<SavedRingtoneDocument?> FindAsync(long userId, long documentId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SavedRingtoneStore(IMongoDatabase mongoDatabase) : ISavedRingtoneStore, ITransientDependency
{
    public const string CollectionName = "saved_ringtones";
    private const string CountersCollectionName = "counters";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<SavedRingtoneDocument> Collection =>
        mongoDatabase.GetCollection<SavedRingtoneDocument>(CollectionName);

    public async Task<List<long>> GetOrderedIdsAsync(long userId, int limit,
        CancellationToken cancellationToken = default)
    {
        var rows = await GetOrderedAsync(userId, limit, cancellationToken);

        return rows.ConvertAll(p => p.DocumentId);
    }

    public async Task<List<SavedRingtoneDocument>> GetOrderedAsync(long userId, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        return await Collection
            .Find(Builders<SavedRingtoneDocument>.Filter.Eq(p => p.UserId, userId))
            .Sort(Builders<SavedRingtoneDocument>.Sort.Descending(p => p.Order))
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(long userId, long documentId, int limit, long originalDocumentId = 0,
        RingtoneAudioInfo? info = null, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var id = SavedRingtoneDocument.MakeId(userId, documentId);
        var existing = await Collection
            .Find(Builders<SavedRingtoneDocument>.Filter.Eq(p => p.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            // Only the conversion mapping and a duration we did not have before may be filled in late; the
            // position stays where it was.
            var updates = new List<UpdateDefinition<SavedRingtoneDocument>>();
            if (originalDocumentId != 0 && originalDocumentId != documentId &&
                existing.OriginalDocumentId != originalDocumentId)
            {
                updates.Add(Builders<SavedRingtoneDocument>.Update.Set(p => p.OriginalDocumentId,
                    originalDocumentId));
            }

            if (info != null && existing.DurationSeconds == 0)
            {
                updates.Add(Builders<SavedRingtoneDocument>.Update.Set(p => p.DurationSeconds,
                    info.DurationSeconds));
                updates.Add(Builders<SavedRingtoneDocument>.Update.Set(p => p.Title, info.Title));
                updates.Add(Builders<SavedRingtoneDocument>.Update.Set(p => p.Performer, info.Performer));
            }

            if (updates.Count > 0)
            {
                await Collection.UpdateOneAsync(
                    Builders<SavedRingtoneDocument>.Filter.Eq(p => p.Id, id),
                    Builders<SavedRingtoneDocument>.Update.Combine(updates),
                    cancellationToken: cancellationToken);
            }

            return false;
        }

        await Collection.InsertOneAsync(new SavedRingtoneDocument
        {
            Id = id,
            UserId = userId,
            DocumentId = documentId,
            OriginalDocumentId = originalDocumentId == documentId ? 0 : originalDocumentId,
            Order = await NextOrderAsync(userId, cancellationToken),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DurationSeconds = info?.DurationSeconds ?? 0,
            Title = info?.Title,
            Performer = info?.Performer
        }, cancellationToken: cancellationToken);

        await EvictAsync(userId, limit, cancellationToken);

        return true;
    }

    public async Task<bool> RemoveAsync(long userId, long documentId,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection.DeleteOneAsync(
            Builders<SavedRingtoneDocument>.Filter.Eq(p => p.Id, SavedRingtoneDocument.MakeId(userId, documentId)),
            cancellationToken);

        return result.DeletedCount > 0;
    }

    public async Task RemoveManyAsync(long userId, IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        await Collection.DeleteManyAsync(
            Builders<SavedRingtoneDocument>.Filter.In(p => p.Id,
                documentIds.Select(p => SavedRingtoneDocument.MakeId(userId, p))),
            cancellationToken);
    }

    public async Task<SavedRingtoneDocument?> FindAsync(long userId, long documentId,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<SavedRingtoneDocument>.Filter;

        return await Collection
            .Find(builder.Eq(p => p.UserId, userId) &
                  (builder.Eq(p => p.DocumentId, documentId) | builder.Eq(p => p.OriginalDocumentId, documentId)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A per-user counter, so putting a sound at the front is a single write rather than a rewrite of
    /// every other row's <c>Order</c>.
    /// </summary>
    private async Task<long> NextOrderAsync(long userId, CancellationToken cancellationToken)
    {
        var counters = mongoDatabase.GetCollection<BsonDocument>(CountersCollectionName);

        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"saved_ringtones_order_{userId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1L),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

        var seq = result.GetValue("seq", BsonNull.Value);

        return seq.BsonType switch
        {
            BsonType.Int64 => seq.AsInt64,
            BsonType.Int32 => seq.AsInt32,
            _ => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private async Task EvictAsync(long userId, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return;
        }

        var filter = Builders<SavedRingtoneDocument>.Filter.Eq(p => p.UserId, userId);
        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (total <= limit)
        {
            return;
        }

        var stale = await Collection
            .Find(filter)
            .Sort(Builders<SavedRingtoneDocument>.Sort.Descending(p => p.Order))
            .Skip(limit)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        await Collection.DeleteManyAsync(
            Builders<SavedRingtoneDocument>.Filter.In(p => p.Id, stale.Select(p => p.Id)),
            cancellationToken);
    }

    /// <summary>Creates the indexes once; a failed attempt is not cached, so the next call retries.</summary>
    private Task EnsureIndexesAsync()
    {
        var pending = Volatile.Read(ref _indexInit);
        if (pending is { IsCompletedSuccessfully: true })
        {
            return pending;
        }

        lock (IndexInitLock)
        {
            if (_indexInit is null || _indexInit.IsFaulted || _indexInit.IsCanceled)
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private async Task CreateIndexesAsync()
    {
        var keys = Builders<SavedRingtoneDocument>.IndexKeys;

        await Collection.Indexes.CreateManyAsync([
            new CreateIndexModel<SavedRingtoneDocument>(keys.Ascending(p => p.UserId).Descending(p => p.Order),
                new CreateIndexOptions { Name = "saved_ringtones_user_order" }),
            new CreateIndexModel<SavedRingtoneDocument>(keys.Ascending(p => p.UserId).Ascending(p => p.OriginalDocumentId),
                new CreateIndexOptions { Name = "saved_ringtones_user_original" })
        ]);
    }
}
