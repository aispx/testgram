using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetStoriesByIDHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetStoriesByID, IStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetStoriesByID obj)
    {
        var peerId = input.UserId;
        var peerType = 0;
        
        if (obj.Id.Count > 0)
        {
            var firstId = obj.Id[0];
            var story = await _storyCollection.Find(s => s.StoryId == firstId && !s.Deleted).FirstOrDefaultAsync();
            if (story != null)
            {
                peerId = story.OwnerPeerId;
                peerType = story.OwnerPeerType;
            }
        }

        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.In(s => s.StoryId, obj.Id.Select(id => (int)id).ToList()),
            filterBuilder.Eq(s => s.OwnerPeerId, peerId),
            filterBuilder.Eq(s => s.OwnerPeerType, peerType),
            filterBuilder.Eq(s => s.Deleted, false),
            filterBuilder.Gt(s => s.ExpireDate, currentTime)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();

        var storyItems = new TVector<IStoryItem>();
        foreach (var doc in stories.OrderByDescending(s => s.StoryId))
        {
            var storyItem = StoryHelper.ConvertToStoryItem(doc, input.UserId);
            storyItems.Add(storyItem);
        }

        var users = new TVector<IUser>();
        if (peerType == 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, [peerId], false, false, input.Layer);
            foreach (var user in userList)
            {
                users.Add((IUser)user);
            }
        }

        return new TStories
        {
            Stories = storyItems,
            Users = users,
            Chats = new TVector<IChat>()
        };
    }
}
