using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class CreateStarGiftCollectionHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCreateStarGiftCollection, MyTelegram.Schema.IStarGiftCollection>
{
    protected override async Task<IStarGiftCollection> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCreateStarGiftCollection obj)
    {
        var col = mongoDatabase.GetCollection<StarGiftCollectionDocument>("star-gift-collections");
        var (userId, channelId) = StarGiftCollectionHelper.GetOwner(input, obj.Peer);

        // Auto-increment collection_id per owner
        var maxId = await col.Find(StarGiftCollectionHelper.OwnerFilter(userId, channelId))
            .SortByDescending(x => x.CollectionId).Limit(1)
            .Project(x => x.CollectionId).FirstOrDefaultAsync();

        var doc = new StarGiftCollectionDocument
        {
            CollectionId = maxId + 1,
            OwnerUserId = userId,
            OwnerChannelId = channelId,
            Title = obj.Title,
            Date = DateTime.UtcNow.ToTimestamp(),
            Items = obj.Stargift?.Select(StarGiftCollectionHelper.ToItem).ToList() ?? []
        };

        await col.InsertOneAsync(doc);
        return StarGiftCollectionHelper.ToTl(doc);
    }
}
