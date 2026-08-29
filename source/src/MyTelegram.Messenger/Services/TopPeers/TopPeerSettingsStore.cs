using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// The peers a user has reset out of their rating, resolved for one user.
/// </summary>
public sealed class TopPeerExclusions
{
    private readonly HashSet<(TopPeerCategory Category, PeerType PeerType, long PeerId)> _perCategory;
    private readonly HashSet<(PeerType PeerType, long PeerId)> _everyCategory;

    internal TopPeerExclusions(
        HashSet<(TopPeerCategory, PeerType, long)> perCategory,
        HashSet<(PeerType, long)> everyCategory)
    {
        _perCategory = perCategory;
        _everyCategory = everyCategory;
    }

    public static TopPeerExclusions Empty { get; } = new([], []);

    public bool IsEmpty => _perCategory.Count == 0 && _everyCategory.Count == 0;

    public bool IsExcluded(TopPeerCategory category, PeerType peerType, long peerId)
    {
        return _everyCategory.Contains((peerType, peerId))
            || _perCategory.Contains((category, peerType, peerId));
    }
}

/// <summary>
/// The per-user state of the <a href="https://corefork.telegram.org/api/top-rating">top peer
/// rating</a>: the <c>contacts.toggleTopPeers</c> opt-out and the exclusions written by
/// <c>contacts.resetTopPeerRating</c>.
/// </summary>
public interface ITopPeerSettingsStore
{
    Task<bool> IsDisabledAsync(long userId, CancellationToken cancellationToken = default);

    Task SetDisabledAsync(long userId, bool disabled, CancellationToken cancellationToken = default);

    Task<TopPeerExclusions> GetExclusionsAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers that a peer must stay out of the rating. <paramref name="category"/> of <c>null</c>
    /// means every category — the shape legacy rows carry, and the fallback for a category
    /// constructor this layer does not model.
    /// </summary>
    Task ExcludePeerAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TopPeerSettingsStore(IMongoDatabase mongoDatabase) : ITopPeerSettingsStore, ITransientDependency
{
    public const string SettingsCollectionName = "top_peers_settings";
    public const string ExcludedCollectionName = "top_peers_excluded";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<BsonDocument> Settings =>
        mongoDatabase.GetCollection<BsonDocument>(SettingsCollectionName);

    private IMongoCollection<BsonDocument> Excluded =>
        mongoDatabase.GetCollection<BsonDocument>(ExcludedCollectionName);

    public async Task<bool> IsDisabledAsync(long userId, CancellationToken cancellationToken = default)
    {
        var doc = await Settings
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync(cancellationToken);

        return doc != null && doc.GetValue("Disabled", BsonBoolean.False).ToBoolean();
    }

    public Task SetDisabledAsync(long userId, bool disabled, CancellationToken cancellationToken = default)
    {
        return Settings.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Set("Disabled", disabled),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<TopPeerExclusions> GetExclusionsAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var docs = await Excluded
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .ToListAsync(cancellationToken);

        if (docs.Count == 0)
        {
            return TopPeerExclusions.Empty;
        }

        var perCategory = new HashSet<(TopPeerCategory, PeerType, long)>();
        var everyCategory = new HashSet<(PeerType, long)>();

        foreach (var doc in docs)
        {
            var peerTypeValue = doc.GetValue("PeerType", BsonNull.Value);
            if (peerTypeValue.BsonType is not (BsonType.Int32 or BsonType.Int64))
            {
                continue;
            }

            var peerType = (PeerType)peerTypeValue.ToInt32();
            var peerId = ReadInt64(doc.GetValue("PeerId", BsonNull.Value));
            if (peerId == 0)
            {
                continue;
            }

            // Rows written before resetTopPeerRating honoured the category carry no Category field and
            // mean "every category"; they are read as such instead of being migrated.
            var categoryValue = doc.GetValue("Category", BsonNull.Value);
            if (categoryValue.BsonType is BsonType.Int32 or BsonType.Int64)
            {
                perCategory.Add(((TopPeerCategory)categoryValue.ToInt32(), peerType, peerId));
            }
            else
            {
                everyCategory.Add((peerType, peerId));
            }
        }

        return new TopPeerExclusions(perCategory, everyCategory);
    }

    public Task ExcludePeerAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default)
    {
        var id = $"{userId}-{(category.HasValue ? ((int)category.Value).ToString() : "all")}-{peerType}-{peerId}";

        var update = Builders<BsonDocument>.Update
            .Set("UserId", userId)
            .Set("PeerType", (int)peerType)
            .Set("PeerId", peerId);

        update = category.HasValue
            ? update.Set("Category", (int)category.Value)
            : update.Unset("Category");

        return Excluded.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    private static long ReadInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }

    /// <summary>Creates the index once; a failed attempt is not cached, so the next call retries.</summary>
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

    private Task CreateIndexesAsync()
    {
        // Every getTopPeers reads this collection for one user.
        return Excluded.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("UserId"),
            new CreateIndexOptions { Name = "top_peers_excluded_user" }));
    }
}
