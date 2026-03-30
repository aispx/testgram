using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsStatusHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsStatus, IStarsStatus>
{
    protected override async Task<IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsStatus obj)
    {
        if (obj.Ton)
        {
            return new TStarsStatus
            {
                Balance = new TStarsTonAmount { Amount = 0 },
                Chats = [], Users = [], History = []
            };
        }

        var userId = input.UserId;
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

        var txDocs = await mongoDatabase.GetCollection<StarsTransactionDocument>("star-transactions")
            .Find(x => x.UserId == userId)
            .SortByDescending(x => x.Date).Limit(5)
            .ToListAsync();

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(txDocs.Select(StarsBalanceHelper.ToTl).ToList()),
            Chats = [], Users = []
        };
    }
}
