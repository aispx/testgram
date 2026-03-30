using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class DeleteAlbumHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestDeleteAlbum, IBool>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestDeleteAlbum obj)
    {
        var (ownerPeerId, ownerPeerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
            Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
            Builders<StoryDocument>.Filter.Eq(s => s.AlbumId, obj.AlbumId)
        );
        
        var update = Builders<StoryDocument>.Update.Unset(s => s.AlbumId).Unset(s => s.AlbumTitle);
        await _storyCollection.UpdateManyAsync(filter, update);

        return new TBoolTrue();
    }
}