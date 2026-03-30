using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftUpgradeAttributesHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetStarGiftUpgradeAttributes, IStarGiftUpgradeAttributes>
{
    protected override async Task<IStarGiftUpgradeAttributes> HandleCoreAsync(IRequestInput input, RequestGetStarGiftUpgradeAttributes obj)
    {
        var gift = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == obj.GiftId).FirstOrDefaultAsync();
        if (gift == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        return new TStarGiftUpgradeAttributes
        {
            Attributes = await UpgradeAttributeHelper.GetAllAsync(mongoDatabase, gift!)
        };
    }
}
