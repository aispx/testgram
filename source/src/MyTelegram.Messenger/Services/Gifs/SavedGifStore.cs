using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// One entry of a user's <a href="https://corefork.telegram.org/api/gifs#saved-gifs">saved GIFs</a>
/// list.
/// </summary>
[BsonIgnoreExtraElements]
public class SavedGifDocument
{
    /// <summary><c>{UserId}:{DocumentId}</c> — one row per user and GIF, so a re-save is an upsert.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }

    public long DocumentId { get; set; }

    /// <summary>
    /// Strictly increasing per user; the list is returned in descending <c>Order</c>, so the newest
    /// save is first and re-saving an existing GIF moves it back to the front simply by taking a
    /// fresh value. Clients hash the list <i>in order</i>, so the front position is not cosmetic —
    /// see <see cref="SavedGifHashHelper"/>.
    /// </summary>
    public long Order { get; set; }

    public int Date { get; set; }

    public static string MakeId(long userId, long documentId) => $"{userId}:{documentId}";
}

/// <summary>
/// Storage behind <c>messages.getSavedGifs</c> / <c>messages.saveGif</c>.
/// See https://corefork.telegram.org/api/gifs#saved-gifs
/// </summary>
public interface ISavedGifStore
{
    /// <summary>
    /// The user's saved GIF ids, newest first, capped at <paramref name="limit"/>. Order is
    /// server-authoritative: clients adopt this sequence verbatim and hash it.
    /// </summary>
    Task<List<long>> GetOrderedIdsAsync(long userId, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the GIF at the front of the list, then evicts the oldest entries past
    /// <paramref name="limit"/> — "If the user adds one more GIF even after the
    /// non-Premium/Premium limit is reached, the server will automatically delete the oldest GIF".
    /// </summary>
    Task AddAsync(long userId, long documentId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Removes the GIF; returns whether a row was actually deleted.</summary>
    Task<bool> RemoveAsync(long userId, long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops rows whose document no longer qualifies as a GIF, so what we store can never disagree
    /// with what we return — a document we hand out but the client discards leaves its list shorter
    /// than ours and the hash mismatched forever.
    /// </summary>
    Task RemoveManyAsync(long userId, IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default);

    /// <summary>How many users have this GIF saved — used to rank the local GIF search corpus.</summary>
    Task<Dictionary<long, int>> CountSaversAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SavedGifStore(IMongoDatabase mongoDatabase) : ISavedGifStore, ITransientDependency
{
    public const string CollectionName = "saved_gifs";
    private const string CountersCollectionName = "counters";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<SavedGifDocument> Collection =>
        mongoDatabase.GetCollection<SavedGifDocument>(CollectionName);

    public async Task<List<long>> GetOrderedIdsAsync(long userId, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        var rows = await Collection
            .Find(Builders<SavedGifDocument>.Filter.Eq(p => p.UserId, userId))
            .Sort(Builders<SavedGifDocument>.Sort.Descending(p => p.Order))
            .Limit(limit)
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(p => p.DocumentId);
    }

    public async Task AddAsync(long userId, long documentId, int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var order = await NextOrderAsync(userId, cancellationToken);

        await Collection.UpdateOneAsync(
            Builders<SavedGifDocument>.Filter.Eq(p => p.Id, SavedGifDocument.MakeId(userId, documentId)),
            Builders<SavedGifDocument>.Update
                .Set(p => p.UserId, userId)
                .Set(p => p.DocumentId, documentId)
                .Set(p => p.Order, order)
                .Set(p => p.Date, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        await EvictAsync(userId, limit, cancellationToken);
    }

    public async Task<bool> RemoveAsync(long userId, long documentId,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection.DeleteOneAsync(
            Builders<SavedGifDocument>.Filter.Eq(p => p.Id, SavedGifDocument.MakeId(userId, documentId)),
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
            Builders<SavedGifDocument>.Filter.In(p => p.Id,
                documentIds.Select(p => SavedGifDocument.MakeId(userId, p))),
            cancellationToken);
    }

    public async Task<Dictionary<long, int>> CountSaversAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var rows = await Collection
            .Find(Builders<SavedGifDocument>.Filter.In(p => p.DocumentId, documentIds))
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<long, int>(documentIds.Count);
        foreach (var row in rows)
        {
            counts[row.DocumentId] = counts.GetValueOrDefault(row.DocumentId) + 1;
        }

        return counts;
    }

    /// <summary>
    /// A per-user counter, so moving a GIF to the front is a single write. Rewriting the
    /// <c>Order</c> of every other row instead (the shape <c>account.saveMusic</c> uses) would be
    /// up to 400 updates per save here.
    /// </summary>
    private async Task<long> NextOrderAsync(long userId, CancellationToken cancellationToken)
    {
        var counters = mongoDatabase.GetCollection<BsonDocument>(CountersCollectionName);

        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"saved_gifs_order_{userId}"),
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

        var filter = Builders<SavedGifDocument>.Filter.Eq(p => p.UserId, userId);
        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (total <= limit)
        {
            return;
        }

        var stale = await Collection
            .Find(filter)
            .Sort(Builders<SavedGifDocument>.Sort.Descending(p => p.Order))
            .Skip(limit)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        await Collection.DeleteManyAsync(
            Builders<SavedGifDocument>.Filter.In(p => p.Id, stale.Select(p => p.Id)),
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
        var keys = Builders<SavedGifDocument>.IndexKeys;

        // Every GIF panel open reads one user's list newest-first, and the local search corpus is
        // ranked by DocumentId across users; neither should be a collection scan.
        await Collection.Indexes.CreateManyAsync([
            new CreateIndexModel<SavedGifDocument>(keys.Ascending(p => p.UserId).Descending(p => p.Order),
                new CreateIndexOptions { Name = "saved_gifs_user_order" }),
            new CreateIndexModel<SavedGifDocument>(keys.Ascending(p => p.DocumentId),
                new CreateIndexOptions { Name = "saved_gifs_document" })
        ]);
    }
}
