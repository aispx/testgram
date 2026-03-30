using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class ReadStoriesHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestReadStories, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyReadsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_reads");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");

    protected override async Task<TVector<int>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestReadStories obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Lte(s => s.StoryId, obj.MaxId)
        );

        var storiesToUpdate = await _storyCollection.Find(filter).ToListAsync();
        var viewCount = storiesToUpdate.Count;

        if (viewCount > 0)
        {
            var update = Builders<StoryDocument>.Update.Inc(s => s.ViewsCount, viewCount);
            await _storyCollection.UpdateManyAsync(filter, update);
        }

        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        foreach (var story in storiesToUpdate)
        {
            var viewFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("storyId", story.StoryId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
                Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType),
                Builders<BsonDocument>.Filter.Eq("viewerUserId", input.UserId)
            );
            
            var existingView = await _storyViewsCollection.Find(viewFilter).FirstOrDefaultAsync();
            if (existingView == null)
            {
                var viewDoc = new BsonDocument
                {
                    { "storyId", story.StoryId },
                    { "ownerPeerId", peerId },
                    { "ownerPeerType", peerType },
                    { "viewerUserId", input.UserId },
                    { "date", currentTime }
                };
                await _storyViewsCollection.InsertOneAsync(viewDoc);
            }
        }

        var readFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerId", peerId),
            Builders<BsonDocument>.Filter.Eq("ownerPeerType", peerType)
        );

        var readDoc = new BsonDocument
        {
            { "userId", input.UserId },
            { "ownerPeerId", peerId },
            { "ownerPeerType", peerType },
            { "maxReadId", obj.MaxId },
            { "date", currentTime }
        };

        var options = new ReplaceOptions { IsUpsert = true };
        await _storyReadsCollection.ReplaceOneAsync(readFilter, readDoc, options);

        return new TVector<int>();
    }
}
