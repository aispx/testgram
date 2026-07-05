using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarsStatusHandler(IMongoDatabase mongoDatabase, IPeerHelper peerHelper, IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsStatus, IStarsStatus>
{
    protected override async Task<IStarsStatus> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsStatus obj)
    {
        const int pageSize = 50;

        // Channel/bot revenue: return channel ledger balance + history.
        if (obj.Peer is not TInputPeerSelf)
        {
            var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
            if (peer.PeerType is PeerType.User or PeerType.Self)
            {
                var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
                if (bot?.Bot != true)
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

                if (!await IsBotOwnerAsync(peer.PeerId, input.UserId))
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

                var botBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, peer.PeerId);
                var botTxDocs = await mongoDatabase.GetCollection<BsonDocument>("star-transactions")
                    .Find(Builders<BsonDocument>.Filter.Eq("UserId", peer.PeerId))
                    .SortByDescending(x => x["Date"])
                    .Limit(pageSize)
                    .ToListAsync();
                var botTransactions = botTxDocs.Select(StarsBalanceHelper.BsonToTl).Cast<IStarsTransaction>().ToList();
                var botNextOffset = botTxDocs.Count == pageSize ? botTxDocs[^1]["_id"].AsString : null;
                return new TStarsStatus
                {
                    Balance = new TStarsAmount { Amount = botBalance },
                    History = new TVector<IStarsTransaction>(botTransactions),
                    NextOffset = botNextOffset,
                    Chats = new TVector<IChat>(),
                    Users = new TVector<IUser>()
                };
            }

            var currency = obj.Ton ? ChannelRevenueHelper.TonCurrency : ChannelRevenueHelper.StarsCurrency;
            var (current, _) = await ChannelRevenueHelper.GetBalanceAsync(mongoDatabase, peer.PeerId, currency);

            var chFilter = Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.PeerId, peer.PeerId)
                           & Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.Currency, currency);
            var chDocs = await mongoDatabase.GetCollection<ChannelRevenueTransactionDocument>(ChannelRevenueHelper.TransactionCollection)
                .Find(chFilter).SortByDescending(x => x.Date).Limit(pageSize).ToListAsync();
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

        var userId = input.UserId;

        if (obj.Ton)
        {
            var tonBalance = await TonBalanceHelper.GetBalanceAsync(mongoDatabase, userId);
            var tonTxDocs = await mongoDatabase.GetCollection<TonTransactionDocument>(TonBalanceHelper.TransactionCollection)
                .Find(Builders<TonTransactionDocument>.Filter.Eq(x => x.UserId, userId))
                .SortByDescending(x => x.Date)
                .Limit(pageSize)
                .ToListAsync();
            var tonNextOffset = tonTxDocs.Count == pageSize ? tonTxDocs[^1].Id : null;
            return new TStarsStatus
            {
                Balance = new TStarsTonAmount { Amount = tonBalance },
                History = new TVector<IStarsTransaction>(tonTxDocs.Select(TonBalanceHelper.ToTl).Cast<IStarsTransaction>().ToList()),
                NextOffset = tonNextOffset,
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, userId);

        // Use BsonDocument instead of typed model to avoid ObjectId deserialization issues
        var txDocs = await mongoDatabase.GetCollection<BsonDocument>("star-transactions")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .SortByDescending(x => x["Date"])
            .Limit(pageSize)
            .ToListAsync();

        var transactions = new List<TStarsTransaction>();
        var slugs = new List<string?>();
        foreach (var doc in txDocs)
        {
            transactions.Add(StarsBalanceHelper.BsonToTl(doc));
            slugs.Add(doc.Contains("StargiftSlug") && !doc["StargiftSlug"].IsBsonNull ? doc["StargiftSlug"].AsString : null);
        }
        await StarsBalanceHelper.HydrateGiftsAsync(mongoDatabase, transactions, slugs);
        var nextOffset = txDocs.Count == pageSize ? txDocs[^1]["_id"].AsString : null;

        return new TStarsStatus
        {
            Balance = new TStarsAmount { Amount = balance },
            History = new TVector<IStarsTransaction>(transactions.Cast<IStarsTransaction>().ToList()),
            NextOffset = nextOffset,
            Chats = new TVector<IChat>(), Users = new TVector<IUser>()
        };
    }

    private async Task<bool> IsBotOwnerAsync(long botUserId, long ownerUserId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .AnyAsync();
    }
}
