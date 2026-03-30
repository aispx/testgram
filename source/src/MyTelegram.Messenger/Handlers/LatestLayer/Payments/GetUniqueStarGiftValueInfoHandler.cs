using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetUniqueStarGiftValueInfoHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetUniqueStarGiftValueInfo, IUniqueStarGiftValueInfo>
{
    protected override async Task<IUniqueStarGiftValueInfo> HandleCoreAsync(IRequestInput input, RequestGetUniqueStarGiftValueInfo obj)
    {
        var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.Slug == obj.Slug).FirstOrDefaultAsync();

        if (doc == null) RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

        // Get original gift stars
        var gift = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == doc!.GiftId).FirstOrDefaultAsync();

        long initialStars = gift?.Stars ?? 0;

        // Count listed on resale
        var listedCount = (int)await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .CountDocumentsAsync(d => d.GiftId == doc!.GiftId && d.ResellStars > 0);

        // Floor price = min resell price
        var minResell = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.GiftId == doc!.GiftId && d.ResellStars > 0)
            .SortBy(d => d.ResellStars)
            .Limit(1)
            .FirstOrDefaultAsync();

        return new TUniqueStarGiftValueInfo
        {
            Currency = "XTR",
            Value = doc!.ResellStars > 0 ? doc.ResellStars : initialStars,
            InitialSaleDate = doc.Date,
            InitialSaleStars = initialStars,
            InitialSalePrice = initialStars,
            ListedCount = listedCount > 0 ? listedCount : null,
            FloorPrice = minResell?.ResellStars > 0 ? minResell.ResellStars : null,
        };
    }
}
