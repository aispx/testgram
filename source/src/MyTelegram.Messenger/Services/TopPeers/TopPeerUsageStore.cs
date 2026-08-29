using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// One recorded use of a peer in one <a href="https://corefork.telegram.org/api/top-rating">top
/// peer</a> category.
/// </summary>
/// <remarks>
/// A row per use rather than a running counter: the rating is a sum of exponentials over the use
/// dates (see <see cref="TopPeerUsageStore.RatingExpression"/>), which is exactly what the clients
/// accumulate locally, and summing it on read keeps the write path a plain insert with no read-modify-write
/// race and no dependency on pipeline updates.
/// </remarks>
[BsonIgnoreExtraElements]
public class TopPeerUsageDocument
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public long UserId { get; set; }

    public int Category { get; set; }

    public int PeerType { get; set; }

    public long PeerId { get; set; }

    /// <summary>Unix seconds — the value the rating math works on.</summary>
    public int Date { get; set; }

    /// <summary>Only here to hang the TTL index off, which needs a BSON date.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Records and reads the uses behind the categories no message can express: picking an inline result,
/// opening a mini app, finishing a call, forwarding somewhere.
/// </summary>
public interface ITopPeerUsageStore
{
    Task RecordAsync(long userId, TopPeerCategory category, PeerType peerType, long peerId, int date,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rating per peer for each requested category, decayed to <paramref name="now"/>. Categories
    /// with no uses are absent from the result.
    /// </summary>
    Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> GetRatingsAsync(long userId,
        IReadOnlyCollection<TopPeerCategory> categories, int now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the counter for a peer, in one category or in all of them. This is a real reset: nothing
    /// remembers the peer afterwards, so it climbs back if the user starts using it again — which is
    /// what <c>contacts.resetTopPeerRating</c> means for a counter the server owns outright.
    /// </summary>
    Task ResetAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TopPeerUsageStore(IMongoDatabase mongoDatabase) : ITopPeerUsageStore, ITransientDependency
{
    public const string CollectionName = "top_peer_usage";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    /// <summary>
    /// <c>Σ exp((date - now) / rating_e_decay)</c> — tdlib's <c>rating_add</c> per use, normalized to
    /// <c>now</c> the way <c>normalize_rating</c> does, so the numbers we hand out are on the same scale
    /// as the increments the client adds to them afterwards.
    /// </summary>
    public static BsonDocument RatingExpression(int now)
    {
        return new BsonDocument("$sum",
            new BsonDocument("$exp",
                new BsonDocument("$divide", new BsonArray
                {
                    new BsonDocument("$subtract", new BsonArray { "$Date", now }),
                    TopPeerRatingConstants.RatingEDecaySeconds
                })));
    }

    private IMongoCollection<TopPeerUsageDocument> Collection =>
        mongoDatabase.GetCollection<TopPeerUsageDocument>(CollectionName);

    public async Task RecordAsync(long userId, TopPeerCategory category, PeerType peerType, long peerId, int date,
        CancellationToken cancellationToken = default)
    {
        if (userId == 0 || peerId == 0)
        {
            return;
        }

        await EnsureIndexesAsync();

        await Collection.InsertOneAsync(new TopPeerUsageDocument
        {
            UserId = userId,
            Category = (int)category,
            PeerType = (int)peerType,
            PeerId = peerId,
            Date = date,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(date).UtcDateTime
        }, cancellationToken: cancellationToken);
    }

    public async Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> GetRatingsAsync(long userId,
        IReadOnlyCollection<TopPeerCategory> categories, int now, CancellationToken cancellationToken = default)
    {
        if (categories.Count == 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        var match = new BsonDocument
        {
            { "UserId", userId },
            { "Category", new BsonDocument("$in", new BsonArray(categories.Select(p => (int)p))) },
            { "Date", new BsonDocument("$gt", now - TopPeerRatingConstants.RatingWindowSeconds) }
        };

        PipelineDefinition<TopPeerUsageDocument, BsonDocument> pipeline = new BsonDocument[]
        {
            new("$match", match),
            new("$group", new BsonDocument
            {
                {
                    "_id", new BsonDocument
                    {
                        { "Category", "$Category" },
                        { "PeerType", "$PeerType" },
                        { "PeerId", "$PeerId" }
                    }
                },
                { "Rating", RatingExpression(now) }
            })
        };

        var grouped = await Collection.Aggregate(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<TopPeerCategory, List<TopPeerRating>>();
        foreach (var doc in grouped)
        {
            var key = doc["_id"].AsBsonDocument;
            var categoryValue = key.GetValue("Category", BsonNull.Value);
            var peerTypeValue = key.GetValue("PeerType", BsonNull.Value);
            if (categoryValue.BsonType is not (BsonType.Int32 or BsonType.Int64) ||
                peerTypeValue.BsonType is not (BsonType.Int32 or BsonType.Int64))
            {
                continue;
            }

            var peerId = TopPeerBson.ReadInt64(key.GetValue("PeerId", BsonNull.Value));
            if (peerId == 0)
            {
                continue;
            }

            var rating = doc.GetValue("Rating", BsonNull.Value);
            if (!rating.IsNumeric)
            {
                continue;
            }

            var category = (TopPeerCategory)categoryValue.ToInt32();
            if (!result.TryGetValue(category, out var list))
            {
                list = [];
                result[category] = list;
            }

            list.Add(new TopPeerRating((PeerType)peerTypeValue.ToInt32(), peerId, rating.ToDouble()));
        }

        return result;
    }

    public async Task ResetAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<TopPeerUsageDocument>.Filter;
        var filter = builder.Eq(p => p.UserId, userId)
                     & builder.Eq(p => p.PeerType, (int)peerType)
                     & builder.Eq(p => p.PeerId, peerId);

        if (category.HasValue)
        {
            filter &= builder.Eq(p => p.Category, (int)category.Value);
        }

        await Collection.DeleteManyAsync(filter, cancellationToken);
    }

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
        var keys = Builders<TopPeerUsageDocument>.IndexKeys;

        await Collection.Indexes.CreateManyAsync([
            // The read is always "one user, these categories, recent only".
            new CreateIndexModel<TopPeerUsageDocument>(
                keys.Ascending(p => p.UserId).Ascending(p => p.Category).Ascending(p => p.Date),
                new CreateIndexOptions { Name = "top_peer_usage_user_category_date" }),
            // Rows older than the rating window can never contribute again.
            new CreateIndexModel<TopPeerUsageDocument>(keys.Ascending(p => p.CreatedAt),
                new CreateIndexOptions
                {
                    Name = "top_peer_usage_ttl",
                    ExpireAfter = TimeSpan.FromSeconds(TopPeerRatingConstants.RatingWindowSeconds)
                })
        ]);
    }
}

internal static class TopPeerBson
{
    public static long ReadInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
