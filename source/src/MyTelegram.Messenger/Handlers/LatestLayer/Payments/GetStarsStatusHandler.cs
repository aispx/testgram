using MongoDB.Bson;
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
                Chats = new TVector<IChat>(), Users = new TVector<IUser>(), History = []
            };
        }

        var userId = input.UserId;
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

        // Use BsonDocument instead of typed model to avoid ObjectId deserialization issues
        var txDocs = await mongoDatabase.GetCollection<BsonDocument>("star-transactions")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .SortByDescending(x => x["Date"])
            .Limit(5)
            .ToListAsync();

        var transactions = new List<IStarsTransaction>();
        foreach (var doc in txDocs)
        {
            transactions.Add(StarsBalanceHelper.BsonToTl(doc));
        }

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(transactions),
            Chats = new TVector<IChat>(), Users = new TVector<IUser>()
        };
    }
}
