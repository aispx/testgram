using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class GetAlbumStoriesHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetAlbumStories, IStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetAlbumStories obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.AlbumId, obj.AlbumId),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter)
            .SortBy(s => s.StoryId)
            .Skip(obj.Offset)
            .Limit(obj.Limit)
            .ToListAsync();

        var storyItems = new TVector<IStoryItem>();
        foreach (var doc in stories)
        {
            storyItems.Add(StoryHelper.ConvertToStoryItem(doc, input.UserId));
        }

        return new TStories
        {
            Stories = storyItems,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Count = stories.Count
        };
    }
}
