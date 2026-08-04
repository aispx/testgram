using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Stores dismissed suggestions in the <c>dismissed_suggestions</c> collection, keyed so that
/// re-dismissing the same suggestion is idempotent.
/// See https://corefork.telegram.org/api/config#suggestions
/// </summary>
public class DismissedSuggestionAppService(IMongoDatabase database)
    : IDismissedSuggestionAppService, ITransientDependency
{
    /// <summary>Guards one-time index creation; the service itself is transient.</summary>
    private static int _indexEnsured;

    private IMongoCollection<BsonDocument> DismissedSuggestions =>
        database.GetCollection<BsonDocument>("dismissed_suggestions");

    public async Task DismissAsync(long selfUserId, Peer? peer, string suggestion)
    {
        await EnsureIndexAsync();

        var peerType = peer == null ? 0 : (int)peer.PeerType;
        var peerId = peer?.PeerId ?? 0;
        var id = $"{selfUserId}:{peerType}:{peerId}:{suggestion}";

        var update = Builders<BsonDocument>.Update
            .SetOnInsert("UserId", selfUserId)
            .SetOnInsert("PeerType", peerType)
            .SetOnInsert("PeerId", peerId)
            .SetOnInsert("Suggestion", suggestion)
            .Set("Date", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await DismissedSuggestions.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    public Task<HashSet<string>> GetDismissedAsync(long selfUserId)
    {
        return GetDismissedCoreAsync(selfUserId, 0, 0);
    }

    public Task<HashSet<string>> GetDismissedAsync(long selfUserId, Peer peer)
    {
        return GetDismissedCoreAsync(selfUserId, (int)peer.PeerType, peer.PeerId);
    }

    public async Task<List<string>> FilterDismissedAsync(long selfUserId, IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return [];
        }

        var dismissed = await GetDismissedAsync(selfUserId);

        return dismissed.Count == 0
            ? [.. suggestions]
            : [.. suggestions.Where(p => !dismissed.Contains(p))];
    }

    private async Task<HashSet<string>> GetDismissedCoreAsync(long selfUserId, int peerType, long peerId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", selfUserId)
                     & Builders<BsonDocument>.Filter.Eq("PeerType", peerType)
                     & Builders<BsonDocument>.Filter.Eq("PeerId", peerId);

        var docs = await DismissedSuggestions
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("Suggestion"))
            .ToListAsync();

        return [.. docs.Select(p => p["Suggestion"].AsString)];
    }

    /// <summary>
    /// help.getPromoData reads this collection on every poll, so the lookup must not be a collection
    /// scan. Created lazily, like the other plain-collection stores in this codebase.
    /// </summary>
    private async Task EnsureIndexAsync()
    {
        if (Interlocked.CompareExchange(ref _indexEnsured, 1, 0) != 0)
        {
            return;
        }

        var keys = Builders<BsonDocument>.IndexKeys
            .Ascending("UserId")
            .Ascending("PeerType")
            .Ascending("PeerId");

        await DismissedSuggestions.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Name = "dismissed_suggestions_user_peer" }));
    }
}
