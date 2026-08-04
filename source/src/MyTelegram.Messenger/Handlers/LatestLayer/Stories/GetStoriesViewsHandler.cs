using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Obtain updated information about a set of <a href="https://corefork.telegram.org/api/stories">stories we posted</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getStoriesViews"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Reports view/forward/reaction counters plus a small recent-viewer preview, so it is restricted to
/// whoever may manage the peer's stories.
/// </para>
/// </remarks>
internal sealed class GetStoriesViewsHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetStoriesViews, MyTelegram.Schema.Stories.IStoryViews>
{
    /// <summary>How many recent viewers clients show as avatars next to the view count.</summary>
    private const int RecentViewerCount = 3;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");
    private readonly IMongoCollection<BsonDocument> _reactionsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reactions");

    protected override async Task<MyTelegram.Schema.Stories.IStoryViews> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestGetStoriesViews obj)
    {
        if (obj.Id == null || obj.Id.Count == 0)
        {
            return new MyTelegram.Schema.Stories.TStoryViews
            {
                Views = new TVector<MyTelegram.Schema.IStoryViews>(),
                Users = new TVector<IUser>()
            };
        }

        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var storyIds = obj.Id.ToList();

        var stories = await _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)))
            .ToListAsync();

        var storyMap = stories.ToDictionary(s => s.StoryId);

        var reactionsByStory = await LoadReactionCountsAsync(peerId, peerType, storyIds);
        var recentViewersByStory = await LoadRecentViewersAsync(peerId, peerType, storyIds);

        var storyViewsList = new TVector<MyTelegram.Schema.IStoryViews>();
        var previewUserIds = new HashSet<long>();

        foreach (var storyId in storyIds)
        {
            if (!storyMap.TryGetValue(storyId, out var story))
            {
                storyViewsList.Add(new MyTelegram.Schema.TStoryViews { ViewsCount = 0 });
                continue;
            }

            recentViewersByStory.TryGetValue(storyId, out var recentViewers);
            reactionsByStory.TryGetValue(storyId, out var reactionCounts);

            if (recentViewers != null)
            {
                foreach (var viewerId in recentViewers)
                {
                    previewUserIds.Add(viewerId);
                }
            }

            storyViewsList.Add(new MyTelegram.Schema.TStoryViews
            {
                HasViewers = story.ViewsCount > 0,
                ViewsCount = story.ViewsCount,
                ForwardsCount = story.ForwardsCount > 0 ? story.ForwardsCount : null,
                ReactionsCount = story.ReactionsCount > 0 ? story.ReactionsCount : null,
                Reactions = reactionCounts is { Count: > 0 } ? new TVector<IReactionCount>(reactionCounts) : null,
                RecentViewers = recentViewers is { Count: > 0 } ? new TVector<long>(recentViewers) : null
            });
        }

        var users = new TVector<IUser>();
        if (previewUserIds.Count > 0)
        {
            // The recent viewers referenced above must be resolvable by the client.
            var userList = await userConverterService.GetUserListAsync(
                input, previewUserIds.ToList(), false, false, input.Layer);
            foreach (var user in userList)
            {
                users.Add((IUser)user);
            }
        }

        return new MyTelegram.Schema.Stories.TStoryViews
        {
            Views = storyViewsList,
            Users = users
        };
    }

    /// <summary>Aggregates <c>story_reactions</c> into per-story reaction counts.</summary>
    private async Task<Dictionary<int, List<IReactionCount>>> LoadReactionCountsAsync(
        long peerId,
        int peerType,
        List<int> storyIds)
    {
        var docs = await _reactionsCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("storyOwnerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("storyOwnerPeerType", peerType),
                Builders<BsonDocument>.Filter.In("storyId", storyIds.Select(id => (BsonValue)id))))
            .ToListAsync();

        var result = new Dictionary<int, List<IReactionCount>>();

        foreach (var group in docs.Where(d => d.Contains("storyId")).GroupBy(d => d["storyId"].AsInt32))
        {
            var counts = group
                .Where(d => d.Contains("reaction"))
                .GroupBy(d => (
                    Value: d["reaction"].AsString,
                    Type: d.Contains("type") ? d["type"].AsString : "emoji"))
                .Select(g => new
                {
                    Reaction = StoryResponseBuilder.ToReaction(g.First()),
                    Count = g.Count()
                })
                .Where(x => x.Reaction != null)
                .OrderByDescending(x => x.Count)
                .Select(x => (IReactionCount)new TReactionCount
                {
                    Reaction = x.Reaction!,
                    Count = x.Count
                })
                .ToList();

            result[group.Key] = counts;
        }

        return result;
    }

    private async Task<Dictionary<int, List<long>>> LoadRecentViewersAsync(
        long peerId,
        int peerType,
        List<int> storyIds)
    {
        var docs = await _storyViewsCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType),
                Builders<BsonDocument>.Filter.In("storyId", storyIds.Select(id => (BsonValue)id))))
            .ToListAsync();

        return docs
            .Where(d => d.Contains("storyId") && d.Contains("viewerUserId"))
            .GroupBy(d => d["storyId"].AsInt32)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(d => d.Contains("date") ? d["date"].AsInt32 : 0)
                    .Select(d => d["viewerUserId"].AsInt64)
                    .Distinct()
                    .Take(RecentViewerCount)
                    .ToList());
    }
}
