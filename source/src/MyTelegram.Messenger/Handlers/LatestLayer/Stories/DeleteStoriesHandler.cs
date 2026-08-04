using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Deletes some posted <a href="https://corefork.telegram.org/api/stories">stories</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.deleteStories"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteStoriesHandler(
    IMongoDatabase mongoDatabase,
    IStoryAccessService storyAccessService,
    IStoryAlbumService storyAlbumService,
    IStoryUpdatesSender storyUpdatesSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestDeleteStories, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<int>> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestDeleteStories obj)
    {
        var (peerId, peerType) =
            await storyAccessService.ResolveOwnedPeerAsync(obj.Peer, input.UserId, StoryRight.Delete);

        var storyIds = obj.Id?.Distinct().ToList() ?? [];
        if (storyIds.Count == 0)
        {
            return new TVector<int>();
        }

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds)
        );

        // Load first, so the response only reports what really existed and belonged to this peer.
        var stories = await _storyCollection.Find(filter).ToListAsync();
        if (stories.Count == 0)
        {
            return new TVector<int>();
        }

        await _storyCollection.UpdateManyAsync(
            filter,
            Builders<StoryDocument>.Update.Set(s => s.Deleted, true));

        // Album covers may have pointed at a story that is now gone.
        foreach (var albumId in stories.SelectMany(s => s.AlbumIds).Distinct())
        {
            await storyAlbumService.RefreshIconAsync(peerId, peerType, albumId);
        }

        var peer = StoryHelper.CreatePeer(peerType, peerId);
        var updates = new TVector<IUpdate>();
        foreach (var story in stories)
        {
            updates.Add(new TUpdateStory
            {
                Peer = peer,
                Story = new TStoryItemDeleted { Id = story.StoryId }
            });
        }

        var deletionUpdates = new TUpdates
        {
            Updates = updates,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };

        // Viewers need to drop the story from their feed, and so do the deleter's other sessions.
        await storyUpdatesSender.PushStoryUpdateAsync(stories[0], deletionUpdates, excludeUserId: input.UserId);
        await storyUpdatesSender.PushToUserAsync(input.UserId, deletionUpdates, input.AuthKeyId);

        return new TVector<int>(stories.Select(s => s.StoryId).ToList());
    }
}
