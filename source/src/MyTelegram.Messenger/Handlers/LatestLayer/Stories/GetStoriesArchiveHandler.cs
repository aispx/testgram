using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class GetStoriesArchiveHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetStoriesArchive, IStories>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IStories> HandleCoreAsync(IRequestInput input, RequestGetStoriesArchive obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Eq(s => s.OwnerPeerId, peerId),
            filterBuilder.Eq(s => s.OwnerPeerType, peerType),
            filterBuilder.Eq(s => s.Archived, true),
            filterBuilder.Eq(s => s.Deleted, false)
        );

        // Add pagination support
        if (obj.OffsetId > 0)
        {
            filter = filterBuilder.And(filter, filterBuilder.Lt(s => s.StoryId, obj.OffsetId));
        }

        var limit = obj.Limit > 0 ? obj.Limit : 100; // Default limit 100

        var stories = await _storyCollection.Find(filter)
            .SortByDescending(s => s.StoryId)
            .Limit(limit)
            .ToListAsync();

        var storyItems = new TVector<IStoryItem>();
        foreach (var story in stories)
        {
            storyItems.Add(StoryHelper.ConvertToStoryItem(story, input.UserId));
        }

        var users = new TVector<IUser>();
        var chats = new TVector<IChat>();

        // Add owner user/channel info to response
        if (peerType == 0 && peerId > 0)
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
            Chats = chats,
            Users = users,
            Count = stories.Count
        };
    }
}
