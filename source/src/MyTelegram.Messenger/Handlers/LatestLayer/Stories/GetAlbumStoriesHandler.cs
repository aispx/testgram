using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Fetch the stories of a <a href="https://corefork.telegram.org/api/stories#story-albums">story album</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getAlbumStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Album stories stay listed after they expire — an album is a profile section, not an active-stories
/// feed — so this deliberately does not filter on <c>ExpireDate</c>.
/// </para>
/// </remarks>
internal sealed class GetAlbumStoriesHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService,
    IStoryResponseBuilder storyResponseBuilder,
    IFileReferenceHelper fileReferenceHelper)
    : RpcResultObjectHandler<RequestGetAlbumStories, IStories>
{
    private const int DefaultLimit = 100;

    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetAlbumStories obj)
    {
        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.AnyEq(s => s.AlbumIds, obj.AlbumId),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var totalCount = await _storyCollection.CountDocumentsAsync(filter);

        var offset = Math.Max(0, obj.Offset);
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, DefaultLimit) : DefaultLimit;

        var album = await storyAlbumService.GetAlbumAsync(peerId, peerType, obj.AlbumId);
        var stories = await _storyCollection.Find(filter).ToListAsync();

        var ordered = OrderStories(stories, album?.StoryOrder)
            .Skip(offset)
            .Take(limit)
            .ToList();

        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        var visible = storyAccessService.FilterVisible(ordered, input.UserId, context);

        var sentReactions = await storyResponseBuilder.GetSentReactionsAsync(
            peerId, peerType, visible.Select(s => s.StoryId), input.UserId);

        var isOwner = await storyAccessService.CanActAsPeerAsync(peerId, peerType, input.UserId, StoryRight.Edit);

        // Real photo sizes, in one query for the page. Albums keep expired stories too, so a
        // guessed size would break the common case here.
        var documents = await storyResponseBuilder.GetStoryDocumentsAsync(visible);
        var photos = await storyResponseBuilder.GetStoryPhotosAsync(visible);

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in visible)
        {
            sentReactions.TryGetValue(story.StoryId, out var sentReaction);
            photos.TryGetValue(story.MediaFileId, out var photo);
            documents.TryGetValue(story.MediaFileId, out var document);
            storyItems.Add(StoryHelper.ConvertToStoryItem(fileReferenceHelper, story, input.UserId, sentReaction, isOwner, photo, document));
        }

        var peers = await storyResponseBuilder.BuildPeersAsync(input, visible);

        return new TStories
        {
            Stories = storyItems,
            Chats = peers.Chats,
            Users = peers.Users,
            Count = (int)totalCount
        };
    }

    /// <summary>
    /// Applies the album's explicit story order; anything not listed follows, newest first.
    /// </summary>
    private static IEnumerable<StoryDocument> OrderStories(List<StoryDocument> stories, List<int>? explicitOrder)
    {
        if (explicitOrder == null || explicitOrder.Count == 0)
        {
            return stories.OrderByDescending(s => s.StoryId);
        }

        var positions = new Dictionary<int, int>();
        for (var i = 0; i < explicitOrder.Count; i++)
        {
            positions[explicitOrder[i]] = i;
        }

        return stories
            .OrderBy(s => positions.TryGetValue(s.StoryId, out var position) ? position : int.MaxValue)
            .ThenByDescending(s => s.StoryId);
    }
}
