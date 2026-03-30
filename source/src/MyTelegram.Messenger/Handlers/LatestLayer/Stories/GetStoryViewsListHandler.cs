using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetStoryViewsListHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetStoryViewsList, IStoryViewsList>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");

    protected override async Task<IStoryViewsList> HandleCoreAsync(IRequestInput input, RequestGetStoryViewsList obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.Id),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var story = await _storyCollection.Find(filter).FirstOrDefaultAsync();

        var viewsCount = story?.ViewsCount ?? 0;

        var viewsFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType),
            Builders<BsonDocument>.Filter.Eq("storyId", obj.Id)
        );
        
        var views = await _storyViewsCollection.Find(viewsFilter).ToListAsync();
        
        var viewerUserIds = views
            .Where(v => v.Contains("viewerUserId"))
            .Select(v => v["viewerUserId"].AsInt64)
            .Distinct()
            .ToList();
        
        var userList = await userConverterService.GetUserListAsync(input, viewerUserIds, false, false, input.Layer);
        
        var storyViews = new TVector<IStoryView>();
        foreach (var user in userList)
        {
            var storyView = new TStoryView
            {
                UserId = user.Id,
                Date = 0
            };
            storyViews.Add(storyView);
        }

        return new TStoryViewsList
        {
            Count = viewsCount,
            ViewsCount = viewsCount,
            ForwardsCount = 0,
            ReactionsCount = 0,
            Views = storyViews,
            Users = new TVector<IUser>(userList.Cast<IUser>()),
            Chats = new TVector<IChat>()
        };
    }
}
