using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class CanSendStoryHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestCanSendStory, ICanSendStoryCount>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _channelMembersCollection =
        mongoDatabase.GetCollection<BsonDocument>("channel_members");

    protected override async Task<ICanSendStoryCount> HandleCoreAsync(IRequestInput input, RequestCanSendStory obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);
        
        var currentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (peerType == 2)
        {
            var memberFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("channelId", peerId),
                Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("isMember", true)
            );
            var member = await _channelMembersCollection.Find(memberFilter).FirstOrDefaultAsync();
            if (member == null)
            {
                return new TCanSendStoryCount
                {
                    CountRemains = 0
                };
            }
        }

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
            Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentDate)
        );

        var count = await _storyCollection.CountDocumentsAsync(filter);
        
        var maxStories = peerType == 2 ? 100 : 100;
        var remaining = Math.Max(0, maxStories - (int)count);

        return new TCanSendStoryCount
        {
            CountRemains = remaining
        };
    }
}
