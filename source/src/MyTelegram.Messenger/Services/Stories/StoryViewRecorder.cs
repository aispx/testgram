using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stats.Ingestion;

namespace MyTelegram.Messenger.Services.Stories;

public interface IStoryViewRecorder
{
    /// <summary>
    /// Records a single view of a story, if it is not already recorded for this viewer and stealth mode
    /// is not active. Returns true when the view counter was actually incremented.
    /// </summary>
    Task<bool> RecordViewAsync(long ownerPeerId, int ownerPeerType, int storyId, long viewerUserId, bool stealthActive);

    /// <summary>Records views for several stories of the same peer. Returns the ids actually counted.</summary>
    Task<List<int>> RecordViewsAsync(
        long ownerPeerId,
        int ownerPeerType,
        IEnumerable<int> storyIds,
        long viewerUserId,
        bool stealthActive);
}

/// <summary>
/// The single place a story view is counted.
/// <para>
/// Views are idempotent per (story, viewer): the counter only moves the first time a given user sees a
/// story. Both stories.incrementStoryViews and stories.readStories funnel through here, so reading a
/// batch of stories cannot inflate each story's counter by the size of the batch.
/// </para>
/// </summary>
public class StoryViewRecorder(IMongoDatabase mongoDatabase, IMetricsStore metricsStore)
    : IStoryViewRecorder, ITransientDependency
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");

    public async Task<bool> RecordViewAsync(
        long ownerPeerId,
        int ownerPeerType,
        int storyId,
        long viewerUserId,
        bool stealthActive)
    {
        var recorded = await RecordViewsAsync(ownerPeerId, ownerPeerType, [storyId], viewerUserId, stealthActive);
        return recorded.Count > 0;
    }

    public async Task<List<int>> RecordViewsAsync(
        long ownerPeerId,
        int ownerPeerType,
        IEnumerable<int> storyIds,
        long viewerUserId,
        bool stealthActive)
    {
        var ids = storyIds.Distinct().ToList();
        var counted = new List<int>();

        if (ids.Count == 0)
        {
            return counted;
        }

        // The owner viewing their own story is not a view.
        if (ownerPeerType == StoryHelper.PeerTypeUser && ownerPeerId == viewerUserId)
        {
            return counted;
        }

        // Stealth mode: the story is still readable, it just leaves no trace.
        if (stealthActive)
        {
            return counted;
        }

        var alreadyViewed = await GetAlreadyViewedAsync(ownerPeerId, ownerPeerType, ids, viewerUserId);
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var utcDay = StatsIngestionTime.ToUtcDay(currentTime);

        foreach (var storyId in ids)
        {
            if (alreadyViewed.Contains(storyId))
            {
                continue;
            }

            var storyFilter = Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
            );

            var update = Builders<StoryDocument>.Update.Inc(s => s.ViewsCount, 1);
            var result = await _storyCollection.UpdateOneAsync(storyFilter, update);

            if (result.MatchedCount == 0)
            {
                // No such story (deleted or wrong owner) — nothing to record.
                continue;
            }

            await _storyViewsCollection.InsertOneAsync(new BsonDocument
            {
                { "storyId", storyId },
                { "ownerPeerId", ownerPeerId },
                { "ownerPeerType", ownerPeerType },
                { "viewerUserId", viewerUserId },
                { "date", currentTime }
            });

            // Stats ingestion: per-story views series (stats.getStoryStats) and, for channel-owned
            // stories, the channel-level story-views counter.
            await metricsStore.RecordAsync(
                new StatsEntityKey(StatsEntityType.Story, ownerPeerId, storyId), StatsMetricNames.Views, utcDay, 1);
            if (ownerPeerType == StoryHelper.PeerTypeChannel)
            {
                await metricsStore.RecordAsync(
                    new StatsEntityKey(StatsEntityType.Channel, ownerPeerId, 0), StatsMetricNames.StoryViews, utcDay, 1);
            }

            counted.Add(storyId);
        }

        return counted;
    }

    private async Task<HashSet<int>> GetAlreadyViewedAsync(
        long ownerPeerId,
        int ownerPeerType,
        List<int> storyIds,
        long viewerUserId)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ownerPeerId", ownerPeerId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerType", ownerPeerType),
            Builders<BsonDocument>.Filter.Eq("viewerUserId", viewerUserId),
            Builders<BsonDocument>.Filter.In("storyId", storyIds.Select(id => (BsonValue)id))
        );

        var existing = await _storyViewsCollection.Find(filter).ToListAsync();

        return existing
            .Where(d => d.Contains("storyId"))
            .Select(d => d["storyId"].AsInt32)
            .ToHashSet();
    }
}
