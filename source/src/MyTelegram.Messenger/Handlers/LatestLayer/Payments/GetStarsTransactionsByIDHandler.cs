using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTransactionsByIDHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService)
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
        var fullHistory = history.Cast<IStarsTransaction>().ToList();
        var (chats, users) = await StarsTransactionPeerHelper.ResolveAsync(input, fullHistory, userConverterService, chatConverterService);
        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(fullHistory),
            Chats = chats,
            Users = users
        };
    }
}
