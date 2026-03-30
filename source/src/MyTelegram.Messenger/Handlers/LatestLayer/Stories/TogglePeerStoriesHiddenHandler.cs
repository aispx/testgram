using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class TogglePeerStoriesHiddenHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestTogglePeerStoriesHidden, IBool>
{
    private readonly IMongoCollection<BsonDocument> _hiddenCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_hidden_peers");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestTogglePeerStoriesHidden obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        if (peerType != 0)
        {
            return new TBoolTrue();
        }

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("peerId", peerId)
        );

        var doc = new BsonDocument
        {
            { "userId", input.UserId },
            { "peerId", peerId },
            { "hidden", obj.Hidden }
        };

        await _hiddenCollection.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
