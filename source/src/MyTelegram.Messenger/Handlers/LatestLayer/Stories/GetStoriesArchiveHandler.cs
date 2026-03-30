using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class GetStoriesArchiveHandler(
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetStoriesArchive, IStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetStoriesArchive obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Archived, true),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter)
            .SortByDescending(s => s.StoryId)
            .ToListAsync();

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in stories)
        {
            storyItems.Add(StoryHelper.ConvertToStoryItem(story, input.UserId));
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
