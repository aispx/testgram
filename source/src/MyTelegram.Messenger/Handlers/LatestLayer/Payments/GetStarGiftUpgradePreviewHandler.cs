using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftUpgradePreviewHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetStarGiftUpgradePreview, IStarGiftUpgradePreview>
{
    protected override async Task<IStarGiftUpgradePreview> HandleCoreAsync(IRequestInput input, RequestGetStarGiftUpgradePreview obj)
    {
        var gift = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == obj.GiftId).FirstOrDefaultAsync();

        var attrs = gift != null
            ? await UpgradeAttributeHelper.GetAllAsync(mongoDatabase, gift)
            : new TVector<IStarGiftAttribute>();

        var prices = new TVector<IStarGiftUpgradePrice>();
        if (gift?.UpgradeStars is > 0)
        {
            prices.Add(new TStarGiftUpgradePrice
            {
                Date = CurrentDate,
                UpgradeStars = gift.UpgradeStars.Value,
            });
        }

        return new TStarGiftUpgradePreview { SampleAttributes = attrs, Prices = prices, NextPrices = [] };
    }
}
