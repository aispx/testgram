using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class InstalledStickerSetStore(IMongoDatabase mongoDatabase)
    : IInstalledStickerSetStore, ITransientDependency
{
    public const string CollectionName = "installed_sticker_sets";
    private const string CountersCollectionName = "counters";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<InstalledStickerSetDocument> Collection =>
        mongoDatabase.GetCollection<InstalledStickerSetDocument>(CollectionName);

    private static FilterDefinition<InstalledStickerSetDocument> ListFilter(long userId, StickerSetType type,
        bool archived)
    {
        return Builders<InstalledStickerSetDocument>.Filter.And(
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.UserId, userId),
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.StickerSetType, type),
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Archived, archived));
    }

    public async Task<List<InstalledStickerSetDocument>> GetAsync(long userId, StickerSetType type, bool archived,
        int limit = 0, long offsetId = 0, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var filter = ListFilter(userId, type, archived);
        if (offsetId > 0)
        {
            filter = Builders<InstalledStickerSetDocument>.Filter.And(filter,
                Builders<InstalledStickerSetDocument>.Filter.Lt(p => p.StickerSetId, offsetId));
        }

        var query = Collection
            .Find(filter)
            .Sort(Builders<InstalledStickerSetDocument>.Sort.Descending(p => p.Order));

        if (limit > 0)
        {
            query = query.Limit(limit);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<long> CountAsync(long userId, StickerSetType type, bool archived,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        return await Collection.CountDocumentsAsync(ListFilter(userId, type, archived),
            cancellationToken: cancellationToken);
    }

    public async Task<Dictionary<long, InstalledStickerSetDocument>> GetOverlayAsync(long userId,
        IReadOnlyCollection<long> stickerSetIds, CancellationToken cancellationToken = default)
    {
        if (stickerSetIds.Count == 0)
        {
            return [];
        }

        var rows = await Collection
            .Find(Builders<InstalledStickerSetDocument>.Filter.In(p => p.Id,
                stickerSetIds.Select(p => InstalledStickerSetDocument.MakeId(userId, p))))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(p => p.StickerSetId);
    }

    public async Task<bool> InstallAsync(long userId, long stickerSetId, StickerSetType type, bool archived,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var id = InstalledStickerSetDocument.MakeId(userId, stickerSetId);
        var existing = await Collection
            .Find(Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

        var order = await NextOrderAsync(userId, cancellationToken);
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var update = Builders<InstalledStickerSetDocument>.Update
            .Set(p => p.UserId, userId)
            .Set(p => p.StickerSetId, stickerSetId)
            .Set(p => p.StickerSetType, type)
            .Set(p => p.Archived, archived)
            .Set(p => p.Order, order)
            .SetOnInsert(p => p.Date, now);

        await Collection.UpdateOneAsync(
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Id, id),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        return existing == null;
    }

    public async Task<bool> UninstallAsync(long userId, long stickerSetId,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection.DeleteOneAsync(
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Id,
                InstalledStickerSetDocument.MakeId(userId, stickerSetId)),
            cancellationToken);

        return result.DeletedCount > 0;
    }

    public Task RemoveForAllUsersAsync(long stickerSetId, CancellationToken cancellationToken = default)
    {
        return Collection.DeleteManyAsync(
            Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.StickerSetId, stickerSetId),
            cancellationToken);
    }

    public async Task<List<long>> SetArchivedAsync(long userId, IReadOnlyCollection<long> stickerSetIds,
        bool archived, CancellationToken cancellationToken = default)
    {
        if (stickerSetIds.Count == 0)
        {
            return [];
        }

        var ids = stickerSetIds.Select(p => InstalledStickerSetDocument.MakeId(userId, p)).ToList();
        var rows = await Collection
            .Find(Builders<InstalledStickerSetDocument>.Filter.In(p => p.Id, ids))
            .ToListAsync(cancellationToken);

        var touched = rows.Where(p => p.Archived != archived).Select(p => p.StickerSetId).ToList();
        if (touched.Count == 0)
        {
            return [];
        }

        await Collection.UpdateManyAsync(
            Builders<InstalledStickerSetDocument>.Filter.In(p => p.Id,
                touched.Select(p => InstalledStickerSetDocument.MakeId(userId, p))),
            Builders<InstalledStickerSetDocument>.Update.Set(p => p.Archived, archived),
            cancellationToken: cancellationToken);

        return touched;
    }

    public async Task ReorderAsync(long userId, StickerSetType type, IReadOnlyList<long> orderedStickerSetIds,
        CancellationToken cancellationToken = default)
    {
        if (orderedStickerSetIds.Count == 0)
        {
            return;
        }

        await EnsureIndexesAsync();

        // The client sends the whole visible list, top first. Descending Order means the first id
        // needs the largest number, so count down from a base above every existing value; that keeps
        // sets the client did not mention (a concurrent install from another session) below the
        // reordered block instead of silently jumping to the top.
        var baseOrder = await NextOrderAsync(userId, cancellationToken, orderedStickerSetIds.Count);

        var writes = new List<WriteModel<InstalledStickerSetDocument>>(orderedStickerSetIds.Count);
        for (var i = 0; i < orderedStickerSetIds.Count; i++)
        {
            writes.Add(new UpdateOneModel<InstalledStickerSetDocument>(
                Builders<InstalledStickerSetDocument>.Filter.And(
                    Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Id,
                        InstalledStickerSetDocument.MakeId(userId, orderedStickerSetIds[i])),
                    Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.StickerSetType, type)),
                Builders<InstalledStickerSetDocument>.Update.Set(p => p.Order, baseOrder - i)));
        }

        await Collection.BulkWriteAsync(writes, cancellationToken: cancellationToken);
    }

    public async Task<bool> MoveToTopAsync(long userId, long stickerSetId,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var order = await NextOrderAsync(userId, cancellationToken);

        var result = await Collection.UpdateOneAsync(
            Builders<InstalledStickerSetDocument>.Filter.And(
                Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Id,
                    InstalledStickerSetDocument.MakeId(userId, stickerSetId)),
                Builders<InstalledStickerSetDocument>.Filter.Eq(p => p.Archived, false)),
            Builders<InstalledStickerSetDocument>.Update.Set(p => p.Order, order),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    public async Task<List<long>> ArchiveOverflowAsync(long userId, StickerSetType type, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        var filter = ListFilter(userId, type, false);
        var total = await Collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (total <= limit)
        {
            return [];
        }

        var overflow = await Collection
            .Find(filter)
            .Sort(Builders<InstalledStickerSetDocument>.Sort.Descending(p => p.Order))
            .Skip(limit)
            .ToListAsync(cancellationToken);

        if (overflow.Count == 0)
        {
            return [];
        }

        await Collection.UpdateManyAsync(
            Builders<InstalledStickerSetDocument>.Filter.In(p => p.Id, overflow.Select(p => p.Id)),
            Builders<InstalledStickerSetDocument>.Update.Set(p => p.Archived, true),
            cancellationToken: cancellationToken);

        return overflow.ConvertAll(p => p.StickerSetId);
    }

    /// <summary>
    /// A per-user counter, so moving a set to the front is a single write instead of rewriting every
    /// other row. <paramref name="reserve"/> claims a contiguous block for a reorder.
    /// </summary>
    private async Task<long> NextOrderAsync(long userId, CancellationToken cancellationToken, int reserve = 1)
    {
        var counters = mongoDatabase.GetCollection<BsonDocument>(CountersCollectionName);

        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"installed_sticker_sets_order_{userId}"),
            Builders<BsonDocument>.Update.Inc("seq", (long)Math.Max(1, reserve)),
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
            if (_indexInit is not { IsCompletedSuccessfully: true })
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private async Task CreateIndexesAsync()
    {
        await Collection.Indexes.CreateManyAsync([
            new CreateIndexModel<InstalledStickerSetDocument>(
                Builders<InstalledStickerSetDocument>.IndexKeys
                    .Ascending(p => p.UserId)
                    .Ascending(p => p.StickerSetType)
                    .Ascending(p => p.Archived)
                    .Descending(p => p.Order)),
            new CreateIndexModel<InstalledStickerSetDocument>(
                Builders<InstalledStickerSetDocument>.IndexKeys.Ascending(p => p.StickerSetId))
        ]);
    }
}
