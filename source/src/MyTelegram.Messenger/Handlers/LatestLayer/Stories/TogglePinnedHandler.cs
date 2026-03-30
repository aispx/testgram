using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class TogglePinnedHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestTogglePinned, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<int>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestTogglePinned obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var storyIds = obj.Id.Select(id => (int)id).ToList();

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, storyIds)
        );

        var update = Builders<StoryDocument>.Update.Set(s => s.Pinned, obj.Pinned);
        await _storyCollection.UpdateManyAsync(filter, update);

        return new TVector<int>(storyIds);
    }
}
