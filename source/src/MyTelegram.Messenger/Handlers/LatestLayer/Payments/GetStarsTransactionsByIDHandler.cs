using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTransactionsByIDHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTransactionsByID, IStarsStatus>
{
    protected override async Task<IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTransactionsByID obj)
    {
        var ids = obj.Id.Select(x => x.Id).ToList();
        var docs = await mongoDatabase.GetCollection<StarsTransactionDocument>("star-transactions")
            .Find(x => x.UserId == input.UserId && ids.Contains(x.TransactionId))
            .ToListAsync();

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        var history = docs.Select(StarsBalanceHelper.ToTl).ToList();
        await StarsBalanceHelper.HydrateGiftsAsync(mongoDatabase, history, docs.Select(d => d.StargiftSlug).ToList());
        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(history.Cast<IStarsTransaction>().ToList()),
            Chats = new TVector<IChat>(), Users = new TVector<IUser>()
        };
    }
}
