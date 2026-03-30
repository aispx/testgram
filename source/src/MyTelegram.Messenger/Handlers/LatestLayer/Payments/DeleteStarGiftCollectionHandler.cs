using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class DeleteStarGiftCollectionHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestDeleteStarGiftCollection, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestDeleteStarGiftCollection obj)
    {
        var col = mongoDatabase.GetCollection<StarGiftCollectionDocument>("star-gift-collections");
        var (userId, channelId) = StarGiftCollectionHelper.GetOwner(input, obj.Peer);

        var filter = StarGiftCollectionHelper.OwnerFilter(userId, channelId)
            & Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.CollectionId, obj.CollectionId);

        await col.DeleteOneAsync(filter);
        return new TBoolTrue();
    }
}
