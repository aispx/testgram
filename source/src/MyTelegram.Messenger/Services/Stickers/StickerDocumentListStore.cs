using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerDocumentListStore(IMongoDatabase mongoDatabase)
    : IStickerDocumentListStore, ITransientDependency
{
    public const string FavedCollectionName = "faved_stickers";
    public const string RecentCollectionName = "recent_stickers";
    private const string CountersCollectionName = "counters";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<BsonDocument> Collection(StickerDocumentListKind kind) =>
        mongoDatabase.GetCollection<BsonDocument>(kind == StickerDocumentListKind.Faved
            ? FavedCollectionName
            : RecentCollectionName);

    /// <summary>
    /// Matches on the natural key rather than on <c>_id</c>: rows written before this store existed carry
    /// a generated <c>ObjectId</c>, and keying on a synthesised <c>_id</c> would duplicate them instead of
    /// updating them.
    /// </summary>
    private static FilterDefinition<BsonDocument> EntryFilter(StickerDocumentListKind kind, long userId,
        long documentId, bool attached)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("DocumentId", documentId));

        return kind == StickerDocumentListKind.Recent
            ? Builders<BsonDocument>.Filter.And(filter, Builders<BsonDocument>.Filter.Eq("Attached", attached))
            : filter;
    }

    private static FilterDefinition<BsonDocument> ListFilter(StickerDocumentListKind kind, long userId,
        bool attached)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

        return kind == StickerDocumentListKind.Recent
            ? Builders<BsonDocument>.Filter.And(filter, Builders<BsonDocument>.Filter.Eq("Attached", attached))
            : filter;
    }

    /// <summary>
    /// Newest first. <c>Order</c> is a per-user counter, so re-adding a sticker within the same second
    /// still moves it to the front; rows written before <c>Order</c> existed have none and sort below the
    /// ones that do, by date, which is the same relative order they had.
    /// </summary>
    private static SortDefinition<BsonDocument> NewestFirst =>
        Builders<BsonDocument>.Sort.Descending("Order").Descending("Date");

    public async Task<List<StickerDocumentListEntry>> GetAsync(StickerDocumentListKind kind, long userId,
        bool attached, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        var rows = await Collection(kind)
            .Find(ListFilter(kind, userId, attached))
            .Sort(NewestFirst)
            .Limit(limit)
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(p => new StickerDocumentListEntry(p.GetInt64("DocumentId"), p.GetInt32("Date")));
    }

    public async Task AddAsync(StickerDocumentListKind kind, long userId, long documentId, bool attached,
        int limit, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var order = await NextOrderAsync(kind, userId, cancellationToken);
        var update = Builders<BsonDocument>.Update
            .Set("UserId", userId)
            .Set("DocumentId", documentId)
            .Set("Order", order)
            .Set("Date", (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (kind == StickerDocumentListKind.Recent)
        {
            update = update.Set("Attached", attached);
        }

        await Collection(kind).UpdateOneAsync(
            EntryFilter(kind, userId, documentId, attached),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        await EvictAsync(kind, userId, attached, limit, cancellationToken);
    }

    public async Task<bool> RemoveAsync(StickerDocumentListKind kind, long userId, long documentId, bool attached,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection(kind)
            .DeleteOneAsync(EntryFilter(kind, userId, documentId, attached), cancellationToken);

        return result.DeletedCount > 0;
    }

    public async Task<bool> ClearAsync(StickerDocumentListKind kind, long userId, bool attached,
        CancellationToken cancellationToken = default)
    {
        var result = await Collection(kind)
            .DeleteManyAsync(ListFilter(kind, userId, attached), cancellationToken);

        return result.DeletedCount > 0;
    }

    public async Task RemoveManyAsync(StickerDocumentListKind kind, long userId,
        IReadOnlyCollection<long> documentIds, bool attached, CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return;
        }

        await Collection(kind).DeleteManyAsync(
            Builders<BsonDocument>.Filter.And(
                ListFilter(kind, userId, attached),
                Builders<BsonDocument>.Filter.In("DocumentId",
                    documentIds.Select(p => (BsonValue)new BsonInt64(p)))),
            cancellationToken);
    }

    private async Task<long> NextOrderAsync(StickerDocumentListKind kind, long userId,
        CancellationToken cancellationToken)
    {
        var counters = mongoDatabase.GetCollection<BsonDocument>(CountersCollectionName);
        var name = kind == StickerDocumentListKind.Faved ? "faved_stickers" : "recent_stickers";

        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"{name}_order_{userId}"),
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

    private async Task EvictAsync(StickerDocumentListKind kind, long userId, bool attached, int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return;
        }

        var collection = Collection(kind);
        var filter = ListFilter(kind, userId, attached);

        var total = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (total <= limit)
        {
            return;
        }

        var stale = await collection
            .Find(filter)
            .Sort(NewestFirst)
            .Skip(limit)
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return;
        }

        await collection.DeleteManyAsync(
            Builders<BsonDocument>.Filter.In("_id", stale.Select(p => p["_id"])),
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
            if (_indexInit is not { IsCompletedSuccessfully: true })
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private async Task CreateIndexesAsync()
    {
        await Collection(StickerDocumentListKind.Faved).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                .Ascending("UserId")
                .Descending("Order")
                .Descending("Date")));

        await Collection(StickerDocumentListKind.Recent).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
                .Ascending("UserId")
                .Ascending("Attached")
                .Descending("Order")
                .Descending("Date")));
    }
}
