using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsTransactionsHandler(IMongoDatabase mongoDatabase, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsTransactions, IStarsStatus>
{
    protected override async Task<IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsTransactions obj)
    {
        var userId = input.UserId;
        var pageSize = obj.Limit > 0 ? obj.Limit : 20;

        // Channel/bot revenue history
        if (obj.Peer is not TInputPeerSelf)
        {
            var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
            var currency = obj.Ton ? ChannelRevenueHelper.TonCurrency : ChannelRevenueHelper.StarsCurrency;
            var (current, _) = await ChannelRevenueHelper.GetBalanceAsync(mongoDatabase, peer.PeerId, currency);

            var chFilter = Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.PeerId, peer.PeerId)
                           & Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.Currency, currency);
            if (obj.Inbound)
                chFilter &= Builders<ChannelRevenueTransactionDocument>.Filter.Gt(x => x.Amount, 0L);
            else if (obj.Outbound)
                chFilter &= Builders<ChannelRevenueTransactionDocument>.Filter.Lt(x => x.Amount, 0L);
            if (!string.IsNullOrEmpty(obj.Offset))
                chFilter &= Builders<ChannelRevenueTransactionDocument>.Filter.Lt(x => x.Id, obj.Offset);

            var chSort = obj.Ascending
                ? Builders<ChannelRevenueTransactionDocument>.Sort.Ascending(x => x.Date)
                : Builders<ChannelRevenueTransactionDocument>.Sort.Descending(x => x.Date);
            var chDocs = await mongoDatabase.GetCollection<ChannelRevenueTransactionDocument>(ChannelRevenueHelper.TransactionCollection)
                .Find(chFilter).Sort(chSort).Limit(pageSize).ToListAsync();
            var chNext = chDocs.Count == pageSize ? chDocs[^1].Id : null;
            return new TStarsStatus
            {
                Balance = ChannelRevenueHelper.BuildAmount(currency, current),
                History = new TVector<IStarsTransaction>(chDocs.Select(ChannelRevenueHelper.ToTl).Cast<IStarsTransaction>().ToList()),
                NextOffset = chNext,
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        if (obj.Ton)
        {
            var tonBalance = await TonBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

            var tonFilter = Builders<TonTransactionDocument>.Filter.Eq(x => x.UserId, userId);
            if (obj.Inbound)
                tonFilter &= Builders<TonTransactionDocument>.Filter.Gt(x => x.Amount, 0L);
            else if (obj.Outbound)
                tonFilter &= Builders<TonTransactionDocument>.Filter.Lt(x => x.Amount, 0L);
            if (!string.IsNullOrEmpty(obj.Offset))
                tonFilter &= Builders<TonTransactionDocument>.Filter.Lt(x => x.Id, obj.Offset);

            var tonSort = obj.Ascending
                ? Builders<TonTransactionDocument>.Sort.Ascending(x => x.Date)
                : Builders<TonTransactionDocument>.Sort.Descending(x => x.Date);

            var tonDocs = await mongoDatabase.GetCollection<TonTransactionDocument>(TonBalanceHelper.TransactionCollection)
                .Find(tonFilter).Sort(tonSort).Limit(pageSize).ToListAsync();

            var tonNextOffset = tonDocs.Count == pageSize ? tonDocs.Last().Id : null;

            return new TStarsStatus
            {
                Balance = new TStarsTonAmount { Amount = tonBalance },
                History = new TVector<IStarsTransaction>(tonDocs.Select(TonBalanceHelper.ToTl).ToList()),
                NextOffset = tonNextOffset,
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

        var filter = Builders<StarsTransactionDocument>.Filter.Eq(x => x.UserId, userId);
        if (obj.Inbound)
            filter &= Builders<StarsTransactionDocument>.Filter.Gt(x => x.Amount, 0);
        else if (obj.Outbound)
            filter &= Builders<StarsTransactionDocument>.Filter.Lt(x => x.Amount, 0);

        // Pagination via offset (string _id)
        if (!string.IsNullOrEmpty(obj.Offset))
            filter &= Builders<StarsTransactionDocument>.Filter.Lt(x => x.Id, obj.Offset);

        var sort = obj.Ascending
            ? Builders<StarsTransactionDocument>.Sort.Ascending(x => x.Date)
            : Builders<StarsTransactionDocument>.Sort.Descending(x => x.Date);

        var docs = await mongoDatabase.GetCollection<StarsTransactionDocument>("star-transactions")
            .Find(filter).Sort(sort).Limit(pageSize)
            .ToListAsync();

        var nextOffset = docs.Count == pageSize ? docs.Last().Id.ToString() : null;

        var history = docs.Select(StarsBalanceHelper.ToTl).ToList();
        await StarsBalanceHelper.HydrateGiftsAsync(mongoDatabase, history, docs.Select(d => d.StargiftSlug).ToList());

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(history.Cast<IStarsTransaction>().ToList()),
            NextOffset = nextOffset,
            Chats = new TVector<IChat>(), Users = new TVector<IUser>()
        };
    }
}
