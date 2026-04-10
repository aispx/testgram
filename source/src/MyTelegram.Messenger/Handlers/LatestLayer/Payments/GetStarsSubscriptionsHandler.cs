using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsSubscriptionsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsSubscriptions, MyTelegram.Schema.Payments.IStarsStatus>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsSubscriptions obj)
    {
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        return new TStarsStatus { Balance = new TStarsAmount { Amount = balance }, Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
    }
}
