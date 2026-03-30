using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ReorderStarGiftCollectionsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestReorderStarGiftCollections, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestReorderStarGiftCollections obj)
    {
        var col = mongoDatabase.GetCollection<StarGiftCollectionDocument>("star-gift-collections");
        var (userId, channelId) = StarGiftCollectionHelper.GetOwner(input, obj.Peer);

        for (int i = 0; i < obj.Order.Count; i++)
        {
            var filter = StarGiftCollectionHelper.OwnerFilter(userId, channelId)
                & Builders<StarGiftCollectionDocument>.Filter.Eq(x => x.CollectionId, obj.Order[i]);
            await col.UpdateOneAsync(filter, Builders<StarGiftCollectionDocument>.Update.Set(x => x.SortOrder, i));
        }

        return new TBoolTrue();
    }
}
