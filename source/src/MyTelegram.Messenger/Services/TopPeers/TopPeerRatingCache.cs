using System.Collections.Concurrent;

namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// A short-lived in-process snapshot of one user's <a href="https://corefork.telegram.org/api/top-rating">top
/// peer rating</a>.
/// </summary>
/// <remarks>
/// Computing the rating is two aggregations, and <c>contacts.getTopPeers</c> is not a once-a-day call in
/// practice: tdesktop re-requests it whenever the search field is opened (its only floor is
/// <c>kRequestTimeLimit</c>, 10 s) and tdlib re-syncs immediately whenever the UI asks for top chats and
/// its own sync has lapsed. Within the TTL the decay difference is far below what any client can observe
/// — <c>exp(60 / rating_e_decay)</c> is 1.000025 — so the snapshot keeps the <c>now</c> it was computed
/// with instead of re-deriving the numbers per request.
/// </remarks>
public interface ITopPeerRatingCache
{
    bool TryGet(long userId, out Dictionary<TopPeerCategory, List<TopPeerRating>> snapshot);

    void Set(long userId, Dictionary<TopPeerCategory, List<TopPeerRating>> snapshot);

    /// <summary>Called from every path that changes the rating, so a use is never invisible for a minute.</summary>
    void Invalidate(long userId);
}

/// <inheritdoc />
public class TopPeerRatingCache : ITopPeerRatingCache, ISingletonDependency
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Above this many cached users the expired entries are swept before adding another.</summary>
    private const int SweepThreshold = 4096;

    private readonly ConcurrentDictionary<long, Entry> _entries = new();

    public bool TryGet(long userId, out Dictionary<TopPeerCategory, List<TopPeerRating>> snapshot)
    {
        if (_entries.TryGetValue(userId, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            snapshot = entry.Snapshot;

            return true;
        }

        snapshot = [];

        return false;
    }

    public void Set(long userId, Dictionary<TopPeerCategory, List<TopPeerRating>> snapshot)
    {
        if (_entries.Count >= SweepThreshold)
        {
            Sweep();
        }

        _entries[userId] = new Entry(snapshot, DateTime.UtcNow.Add(Ttl));
    }

    public void Invalidate(long userId)
    {
        _entries.TryRemove(userId, out _);
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(Dictionary<TopPeerCategory, List<TopPeerRating>> Snapshot, DateTime ExpiresAt);
}
