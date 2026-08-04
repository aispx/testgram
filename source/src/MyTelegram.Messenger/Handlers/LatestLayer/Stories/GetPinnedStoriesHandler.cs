using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the <a href="https://corefork.telegram.org/api/stories#pinned-or-archived-stories">stories pinned on a peer's profile</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getPinnedStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Pinned stories remain on the profile after they expire — that is the point of pinning — so this
/// deliberately does not filter on <c>ExpireDate</c>.
/// </para>
/// </remarks>
internal sealed class GetPinnedStoriesHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryResponseBuilder storyResponseBuilder)
    : RpcResultObjectHandler<RequestGetPinnedStories, IStories>
{
    private const int MaxLimit = 100;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetPinnedStories obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var filterBuilder = Builders<StoryDocument>.Filter;
        var baseFilter = filterBuilder.And(
            filterBuilder.Eq(s => s.OwnerPeerId, peerId),
            filterBuilder.Eq(s => s.OwnerPeerType, peerType),
            filterBuilder.Eq(s => s.Pinned, true),
            filterBuilder.Eq(s => s.Deleted, false)
        );

        var totalCount = await _storyCollection.CountDocumentsAsync(baseFilter);

        var pageFilter = obj.OffsetId > 0
            ? filterBuilder.And(baseFilter, filterBuilder.Lt(s => s.StoryId, obj.OffsetId))
            : baseFilter;

        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : MaxLimit;

        var stories = await _storyCollection.Find(pageFilter)
            // Stories pinned to the top of the profile come first, newest first within each section.
            .SortByDescending(s => s.PinnedToTop)
            .ThenByDescending(s => s.StoryId)
            .Limit(limit)
            .ToListAsync();

        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        var visible = storyAccessService.FilterVisible(stories, input.UserId, context);

        var isOwner = await storyAccessService.CanActAsPeerAsync(peerId, peerType, input.UserId, StoryRight.Edit);

        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(
            peerId, peerType, visible.Select(s => s.StoryId), input.UserId);

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in visible)
        {
            sentReactions.TryGetValue(story.StoryId, out var sentReaction);
            storyItems.Add(StoryHelper.ConvertToStoryItem(story, input.UserId, sentReaction, isOwner));
        }

        var pinnedToTopIds = visible.Where(s => s.PinnedToTop).Select(s => s.StoryId).ToList();
        var peers = await storyResponseBuilder.BuildPeersAsync(input, visible, [peerId]);

        return new TStories
        {
            Stories = storyItems,
            PinnedToTop = pinnedToTopIds.Count > 0 ? new TVector<int>(pinnedToTopIds) : null,
            Chats = peers.Chats,
            Users = peers.Users,
            Count = (int)totalCount
        };
    }
}
