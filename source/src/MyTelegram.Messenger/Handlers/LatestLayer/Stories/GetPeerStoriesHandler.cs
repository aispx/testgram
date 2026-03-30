using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetPeerStoriesHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetPeerStories, MyTelegram.Schema.Stories.IPeerStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");

    protected override async Task<MyTelegram.Schema.Stories.IPeerStories> HandleCoreAsync(IRequestInput input, RequestGetPeerStories obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);
        
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Lte(s => s.Date, currentTime),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter)
            .SortByDescending(s => s.StoryId)
            .ToListAsync();

        var storyItems = new TVector<IStoryItem>();

        foreach (var story in stories)
        {
            var storyItem = StoryHelper.ConvertToStoryItem(story, input.UserId);
            storyItems.Add(storyItem);
        }

        var readFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType)
        );
        var readDoc = await _storyReadsCollection.Find(readFilter).FirstOrDefaultAsync();
        int maxReadId = readDoc != null && readDoc.Contains("maxReadId") ? readDoc["maxReadId"].AsInt32 : 0;

        var users = new TVector<IUser>();
        var chats = new TVector<IChat>();

        if (peerType == 0 && peerId > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, [peerId], false, false, input.Layer);
            foreach (var user in userList)
            {
                users.Add((IUser)user);
            }
        }

        var peer = new MyTelegram.Schema.TPeerStories
        {
            Peer = StoryHelper.CreatePeer(peerType, peerId),
            Stories = storyItems,
            MaxReadId = maxReadId > 0 ? maxReadId : null
        };

        return new MyTelegram.Schema.Stories.TPeerStories
        {
            Stories = peer,
            Chats = chats,
            Users = users
        };
    }
}
