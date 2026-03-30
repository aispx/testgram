using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetStoriesViewsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetStoriesViews, MyTelegram.Schema.Stories.IStoryViews>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<MyTelegram.Schema.Stories.IStoryViews> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestGetStoriesViews obj)
    {
        if (obj.Id == null || obj.Id.Count == 0)
        {
            return new MyTelegram.Schema.Stories.TStoryViews
            {
                Views = new TVector<MyTelegram.Schema.IStoryViews>(),
                Users = new TVector<IUser>()
            };
        }

        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, obj.Id.ToList()),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var stories = await _storyCollection.Find(filter).ToListAsync();
        var storyMap = stories.ToDictionary(s => s.StoryId);

        var storyViewsList = new TVector<MyTelegram.Schema.IStoryViews>();

        foreach (var storyId in obj.Id)
        {
            if (storyMap.TryGetValue(storyId, out var story))
            {
                var storyViews = new MyTelegram.Schema.TStoryViews
                {
                    ViewsCount = story.ViewsCount,
                    ForwardsCount = null,
                    ReactionsCount = null,
                    Reactions = new TVector<IReactionCount>()
                };
                storyViewsList.Add(storyViews);
            }
            else
            {
                var storyViews = new MyTelegram.Schema.TStoryViews
                {
                    ViewsCount = 0,
                    ForwardsCount = null,
                    ReactionsCount = null
                };
                storyViewsList.Add(storyViews);
            }
        }

        return new MyTelegram.Schema.Stories.TStoryViews
        {
            Views = storyViewsList,
            Users = new TVector<IUser>()
        };
    }
}
