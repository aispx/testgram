using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the full active <a href="https://corefork.telegram.org/api/stories">story list</a> of a specific peer.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getPeerStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPeerStoriesHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryResponseBuilder storyResponseBuilder,
    IFileReferenceHelper fileReferenceHelper)
    : RpcResultObjectHandler<RequestGetPeerStories, MyTelegram.Schema.Stories.IPeerStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");

    protected override async Task<MyTelegram.Schema.Stories.IPeerStories> HandleCoreAsync(
        IRequestInput input,
        RequestGetPeerStories obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter)
            .SortBy(s => s.StoryId)
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
            storyItems.Add(StoryHelper.ConvertToStoryItem(fileReferenceHelper, story, input.UserId, sentReaction, isOwner));
        }

        var maxReadId = await GetMaxReadIdAsync(input.UserId, peerId, peerType);
        var peers = await storyResponseBuilder.BuildPeersAsync(input, visible, [peerId]);

        return new MyTelegram.Schema.Stories.TPeerStories
        {
            Stories = new MyTelegram.Schema.TPeerStories
            {
                Peer = StoryHelper.CreatePeer(peerType, peerId),
                Stories = storyItems,
                MaxReadId = maxReadId > 0 ? maxReadId : null
            },
            Chats = peers.Chats,
            Users = peers.Users
        };
    }

    private async Task<int> GetMaxReadIdAsync(long userId, long peerId, int peerType)
    {
        var readDoc = await _storyReadsCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("userId", userId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType)))
            .FirstOrDefaultAsync();

        return readDoc != null && readDoc.Contains("maxReadId") ? readDoc["maxReadId"].AsInt32 : 0;
    }
}
