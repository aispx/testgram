using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the <a href="https://corefork.telegram.org/api/stories#pinned-or-archived-stories">stories archive</a>
/// of a peer — every story ever posted, expired or not.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getStoriesArchive"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// The archive is the owner's own view of their stories, so this requires the right to manage them.
/// </para>
/// </remarks>
internal sealed class GetStoriesArchiveHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryResponseBuilder storyResponseBuilder)
    : RpcResultObjectHandler<RequestGetStoriesArchive, IStories>
{
    private const int MaxLimit = 100;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetStoriesArchive obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Edit);

        var filterBuilder = Builders<StoryDocument>.Filter;
        var baseFilter = filterBuilder.And(
            filterBuilder.Eq(s => s.OwnerPeerId, peerId),
            filterBuilder.Eq(s => s.OwnerPeerType, peerType),
            filterBuilder.Eq(s => s.Archived, true),
            filterBuilder.Eq(s => s.Deleted, false)
        );

        var totalCount = await _storyCollection.CountDocumentsAsync(baseFilter);

        var pageFilter = obj.OffsetId > 0
            ? filterBuilder.And(baseFilter, filterBuilder.Lt(s => s.StoryId, obj.OffsetId))
            : baseFilter;

        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : MaxLimit;

        var stories = await _storyCollection.Find(pageFilter)
            .SortByDescending(s => s.StoryId)
            .Limit(limit)
            .ToListAsync();

        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(
            peerId, peerType, stories.Select(s => s.StoryId), input.UserId);

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in stories)
        {
            sentReactions.TryGetValue(story.StoryId, out var sentReaction);
            // The archive is only visible to the owner, so the privacy rules are theirs to see.
            storyItems.Add(StoryHelper.ConvertToStoryItem(story, input.UserId, sentReaction, includePrivacy: true));
        }

        var peers = await storyResponseBuilder.BuildPeersAsync(input, stories, [peerId]);

        return new TStories
        {
            Stories = storyItems,
            Chats = peers.Chats,
            Users = peers.Users,
            Count = (int)totalCount
        };
    }
}
