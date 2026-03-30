using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTransactionsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTransactions, IStarsStatus>
{
    protected override async Task<IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTransactions obj)
    {
        var userId = input.UserId;

        // If peer is a bot (not self), return empty — bot revenue is not supported
        if (obj.Peer is not TInputPeerSelf)
        {
            return new TStarsStatus { Balance = new TStarsAmount { Amount = 0 }, History = new TVector<IStarsTransaction>([]), NextOffset = null, Chats = [], Users = [] };
        }
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

        var filter = Builders<StarsTransactionDocument>.Filter.Eq(x => x.UserId, userId);
        if (obj.Inbound)
            filter &= Builders<StarsTransactionDocument>.Filter.Gt(x => x.Amount, 0);
        else if (obj.Outbound)
            filter &= Builders<StarsTransactionDocument>.Filter.Lt(x => x.Amount, 0);

        // Pagination via offset (ObjectId string)
        if (!string.IsNullOrEmpty(obj.Offset) && MongoDB.Bson.ObjectId.TryParse(obj.Offset, out var offsetId))
            filter &= Builders<StarsTransactionDocument>.Filter.Lt(x => x.Id, offsetId);

        var sort = obj.Ascending
            ? Builders<StarsTransactionDocument>.Sort.Ascending(x => x.Date)
            : Builders<StarsTransactionDocument>.Sort.Descending(x => x.Date);

        var docs = await mongoDatabase.GetCollection<StarsTransactionDocument>("star-transactions")
            .Find(filter).Sort(sort).Limit(obj.Limit > 0 ? obj.Limit : 20)
            .ToListAsync();

        var nextOffset = docs.Count == (obj.Limit > 0 ? obj.Limit : 20)
            ? docs.Last().Id.ToString()
            : null;

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(docs.Select(StarsBalanceHelper.ToTl).ToList()),
            NextOffset = nextOffset,
            Chats = [], Users = []
        };
    }
}
