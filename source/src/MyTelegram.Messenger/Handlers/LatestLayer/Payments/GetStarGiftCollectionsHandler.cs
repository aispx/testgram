using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftCollectionsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftCollections, IStarGiftCollections>
{
    protected override async Task<IStarGiftCollections> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarGiftCollections obj)
    {
        var col = mongoDatabase.GetCollection<StarGiftCollectionDocument>("star-gift-collections");
        var (userId, channelId) = StarGiftCollectionHelper.GetOwner(input, obj.Peer);

        var docs = await col.Find(StarGiftCollectionHelper.OwnerFilter(userId, channelId))
            .SortBy(x => x.SortOrder).ThenBy(x => x.Date)
            .ToListAsync();

        // Hash check: XOR of all collection hashes
        if (obj.Hash != 0)
        {
            long hash = docs.Aggregate(0L, (acc, d) => acc ^ ((TStarGiftCollection)StarGiftCollectionHelper.ToTl(d)).Hash);
            if (hash == obj.Hash)
                return new TStarGiftCollectionsNotModified();
        }

        return new TStarGiftCollections
        {
            Collections = new TVector<IStarGiftCollection>(docs.Select(StarGiftCollectionHelper.ToTl).ToList())
        };
    }
}
