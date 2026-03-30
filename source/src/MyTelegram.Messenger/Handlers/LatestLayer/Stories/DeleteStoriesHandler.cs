using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class DeleteStoriesHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestDeleteStories, TVector<int>>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<TVector<int>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestDeleteStories obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
            Builders<StoryDocument>.Filter.In(s => s.StoryId, obj.Id.ToList())
        );

        var update = Builders<StoryDocument>.Update.Set(s => s.Deleted, true);
        await _storyCollection.UpdateManyAsync(filter, update);

        return new TVector<int>(obj.Id.ToList());
    }
}
