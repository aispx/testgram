using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Get the IDs of the maximum read stories of a set of peers.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getPeerMaxIDs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// One entry per requested peer, in the same order, so the client can zip the results back to its input.
/// </para>
/// </remarks>
internal sealed class GetPeerMaxIDsHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetPeerMaxIDs, TVector<IRecentStory>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<IRecentStory>> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestGetPeerMaxIDs obj)
    {
        var result = new TVector<IRecentStory>();
        if (obj.Id == null || obj.Id.Count == 0)
        {
            return result;
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var resolved = obj.Id.Select(peer => StoryHelper.ResolvePeer(peer, input.UserId)).ToList();

        // One query for every requested peer rather than one per peer.
        var stories = await _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.In(s => s.OwnerPeerId, resolved.Select(r => r.peerId).Distinct()),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
                Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
                Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)))
            .ToListAsync();

        var context = await storyAccessService.GetViewerContextAsync(
            input.UserId,
            stories.Where(s => s.OwnerPeerType == StoryHelper.PeerTypeUser).Select(s => s.OwnerPeerId));

        var visible = storyAccessService.FilterVisible(stories, input.UserId, context);

        var latestByPeer = visible
            .GroupBy(s => (s.OwnerPeerId, s.OwnerPeerType))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.StoryId).First());

        foreach (var key in resolved)
        {
            if (latestByPeer.TryGetValue(key, out var latest))
            {
                result.Add(new TRecentStory
                {
                    MaxId = latest.StoryId,
                    Live = latest.IsLive
                });
            }
            else
            {
                result.Add(new TRecentStory());
            }
        }

        return result;
    }
}
