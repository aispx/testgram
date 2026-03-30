using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class IncrementStoryViewsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestIncrementStoryViews, IBool>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestIncrementStoryViews obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var storyIds = obj.Id.Select(id => (int)id).ToList();
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );
        var update = Builders<StoryDocument>.Update.Inc(s => s.ViewsCount, 1);
        await _storyCollection.UpdateManyAsync(filter, update);

        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var storyId in storyIds)
        {
            var viewFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("storyId", storyId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType),
                Builders<BsonDocument>.Filter.Eq("viewerUserId", input.UserId)
            );

            var existingView = await _storyViewsCollection.Find(viewFilter).FirstOrDefaultAsync();
            if (existingView == null)
            {
                var viewDoc = new BsonDocument
                {
                    { "storyId", storyId },
                    { "ownerPeerId", peerId },
                    { "ownerPeerType", peerType },
                    { "viewerUserId", input.UserId },
                    { "date", currentTime }
                };
                await _storyViewsCollection.InsertOneAsync(viewDoc);
            }
        }

        return new TBoolTrue();
    }
}
