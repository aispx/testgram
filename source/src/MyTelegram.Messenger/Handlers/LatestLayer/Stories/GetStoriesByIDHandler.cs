using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Obtain full info about a set of <a href="https://corefork.telegram.org/api/stories#pinned-or-archived-stories">stories</a> by their IDs.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_EMPTY You specified no story IDs.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getStoriesByID"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Expired stories are still returned here — the client asks for specific ids, e.g. to render a story
/// referenced by a message — but privacy is still applied.
/// </para>
/// </remarks>
internal sealed class GetStoriesByIDHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryResponseBuilder storyResponseBuilder,
    IFileReferenceHelper fileReferenceHelper)
    : RpcResultObjectHandler<RequestGetStoriesByID, IStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetStoriesByID obj)
    {
        if (obj.Id == null || obj.Id.Count == 0)
        {
            RpcErrors.RpcErrors400.StoryIdEmpty.ThrowRpcError();
        }

        // The peer comes from the request; resolving stories by id alone would return another peer's
        // stories whenever ids collide across owners.
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var storyIds = obj.Id!.Distinct().ToList();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();

        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        var visible = storyAccessService.FilterVisible(stories, input.UserId, context);

        var isOwner = await storyAccessService.CanActAsPeerAsync(peerId, peerType, input.UserId, StoryRight.Edit);

        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(
            peerId, peerType, visible.Select(s => s.StoryId), input.UserId);

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in visible.OrderByDescending(s => s.StoryId))
        {
            sentReactions.TryGetValue(story.StoryId, out var sentReaction);
            storyItems.Add(StoryHelper.ConvertToStoryItem(fileReferenceHelper, story, input.UserId, sentReaction, isOwner));
        }

        var peers = await storyResponseBuilder.BuildPeersAsync(input, visible, [peerId]);

        return new TStories
        {
            Stories = storyItems,
            Users = peers.Users,
            Chats = peers.Chats,
            Count = storyItems.Count
        };
    }
}
