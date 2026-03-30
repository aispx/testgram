using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetUniqueStarGiftHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetUniqueStarGift, IUniqueStarGift>
{
    protected override async Task<IUniqueStarGift> HandleCoreAsync(IRequestInput input, RequestGetUniqueStarGift obj)
    {
        var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.Slug == obj.Slug).FirstOrDefaultAsync();

        if (doc == null) RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

        return new TUniqueStarGift { Gift = UniqueStarGiftHelper.ToTl(doc!), Chats = [], Users = [] };
    }
}
