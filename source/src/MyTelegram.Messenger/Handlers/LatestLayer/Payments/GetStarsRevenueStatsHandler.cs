using MyTelegram.Schema.Payments;

using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/stars">Telegram Star revenue statistics »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getStarsRevenueStats"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStarsRevenueStatsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IGraphBuilder graphBuilder,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsRevenueStats, MyTelegram.Schema.Payments.IStarsRevenueStats>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsRevenueStats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsRevenueStats obj)
    {
        var currency = obj.Ton ? ChannelRevenueHelper.TonCurrency : ChannelRevenueHelper.StarsCurrency;
        var nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usdRate = obj.Ton ? options.CurrentValue.Rates.TonUsdRate : options.CurrentValue.Rates.StarsUsdRate;

        // Resolve target peer: own wallet (inputPeerSelf / own user id), an owned bot, or a channel.
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        if (peer!.PeerType is PeerType.User or PeerType.Self)
        {
            // The Stars balance screen ("Stars" button under the balance) opens
            // BotStarsActivity(TYPE_STARS, clientUserId) and requests stats for the user's OWN
            // wallet — the client has a dedicated self mode for this (self == botId ==
            // clientUserId). Rejecting it with PEER_ID_INVALID leaves the whole screen without
            // status, so serve the caller's own wallet instead of demanding a bot peer.
            if (peer.PeerId == input.UserId)
            {
                var selfCurrent = obj.Ton
                    ? await TonBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId)
                    : await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
                var selfOverall = obj.Ton
                    ? selfCurrent
                    : await GetOverallStarsRevenueAsync(input.UserId);

                return new TStarsRevenueStats
                {
                    Status = new TStarsRevenueStatus
                    {
                        CurrentBalance = ChannelRevenueHelper.BuildAmount(currency, selfCurrent),
                        AvailableBalance = ChannelRevenueHelper.BuildAmount(currency, selfCurrent),
                        OverallRevenue = ChannelRevenueHelper.BuildAmount(currency, selfOverall),
                        WithdrawalEnabled = selfCurrent > 0 && (obj.Ton || selfCurrent >= StarsWithdrawalMin),
                    },
                    RevenueGraph = obj.Ton
                        ? await BuildRevenueGraphAsync(input.UserId, currency, obj.Dark, nowUnix)
                        : await BuildBotStarsRevenueGraphAsync(input.UserId, obj.Dark, nowUnix),
                    UsdRate = usdRate,
                };
            }

            var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
            if (bot?.Bot != true)
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

            if (!await IsBotOwnerAsync(peer.PeerId, input.UserId))
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

            var botCurrent = obj.Ton
                ? await TonBalanceHelper.GetBalanceAsync(mongoDatabase, peer.PeerId)
                : await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, peer.PeerId);
            var botOverall = obj.Ton
                ? botCurrent
                : await GetOverallStarsRevenueAsync(peer.PeerId);

            return new TStarsRevenueStats
            {
                Status = new TStarsRevenueStatus
                {
                    CurrentBalance = ChannelRevenueHelper.BuildAmount(currency, botCurrent),
                    AvailableBalance = ChannelRevenueHelper.BuildAmount(currency, botCurrent),
                    OverallRevenue = ChannelRevenueHelper.BuildAmount(currency, botOverall),
                    WithdrawalEnabled = botCurrent > 0 && (obj.Ton || botCurrent >= StarsWithdrawalMin),
                },
                RevenueGraph = obj.Ton
                    ? await BuildRevenueGraphAsync(peer.PeerId, currency, obj.Dark, nowUnix)
                    : await BuildBotStarsRevenueGraphAsync(peer.PeerId, obj.Dark, nowUnix),
                UsdRate = usdRate,
            };
        }

        long peerId = peer.PeerType switch
        {
            PeerType.Channel => peer.PeerId,
            _ => 0
        };

        var (current, overall) = await ChannelRevenueHelper.GetBalanceAsync(mongoDatabase, peerId, currency);

        // Build a simple last-30-days revenue graph from transactions
        var graph = await BuildRevenueGraphAsync(peerId, currency, obj.Dark, nowUnix);

        // Min withdrawal threshold (only for stars; TON has no min in client)
        var withdrawalEnabled = current > 0 && (obj.Ton || current >= 1000);

        return new TStarsRevenueStats
        {
            Status = new TStarsRevenueStatus
            {
                CurrentBalance = ChannelRevenueHelper.BuildAmount(currency, current),
                AvailableBalance = ChannelRevenueHelper.BuildAmount(currency, current),
                OverallRevenue = ChannelRevenueHelper.BuildAmount(currency, overall),
                WithdrawalEnabled = withdrawalEnabled,
            },
            RevenueGraph = graph,
            UsdRate = usdRate,
        };
    }

    /// <summary>
    /// Minimum withdrawable Stars balance, mirroring the client's <c>stars_revenue_withdrawal_min</c>
    /// app config default. TON has no minimum in the client.
    /// </summary>
    private const long StarsWithdrawalMin = 1000;

    private async Task<long> GetOverallStarsRevenueAsync(long botUserId)
    {
        var docs = await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("star-transactions")
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<MongoDB.Bson.BsonDocument>.Filter.Gt("Amount", 0L))
            .ToListAsync();

        return docs.Sum(x => x.TryGetValue("Amount", out var amount) && amount.IsInt64 ? amount.AsInt64 : 0L);
    }

    private async Task<IStatsGraph> BuildBotStarsRevenueGraphAsync(long botUserId, bool dark, int nowUnix)
    {
        var since = RevenueGraphHelper.WindowStartDay(nowUnix);
        var docs = await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("star-transactions")
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<MongoDB.Bson.BsonDocument>.Filter.Gt("Amount", 0L) &
                  Builders<MongoDB.Bson.BsonDocument>.Filter.Gte("Date", since))
            .ToListAsync();

        var totals = new Dictionary<long, long>();
        foreach (var doc in docs)
        {
            if (!doc.TryGetValue("Date", out var date) || !(date.IsInt32 || date.IsInt64))
            {
                continue;
            }

            var timestamp = date.IsInt64 ? date.AsInt64 : date.AsInt32;
            var day = timestamp - timestamp % 86400;
            var amount = doc.TryGetValue("Amount", out var value) && value.IsInt64 ? value.AsInt64 : 0L;
            totals[day] = totals.GetValueOrDefault(day) + amount;
        }

        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(totals, nowUnix);
        return await graphBuilder.BuildInlineAsync(spec, dark, $"stars-revenue:{botUserId}:{ChannelRevenueHelper.StarsCurrency}", nowUnix);
    }

    private async Task<bool> IsBotOwnerAsync(long botUserId, long ownerUserId)
    {
        return await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("bot-owners")
            .Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .AnyAsync();
    }

    private async Task<IStatsGraph> BuildRevenueGraphAsync(long peerId, string currency, bool dark, int nowUnix)
    {
        var since = RevenueGraphHelper.WindowStartDay(nowUnix);
        var filter = Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.PeerId, peerId)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.Currency, currency)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Gte(x => x.Date, since)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Gt(x => x.Amount, 0L);
        var docs = await mongoDatabase.GetCollection<ChannelRevenueTransactionDocument>(ChannelRevenueHelper.TransactionCollection)
            .Find(filter).ToListAsync();

        var totals = new Dictionary<long, long>();
        foreach (var doc in docs)
        {
            var day = (long)doc.Date - doc.Date % 86400;
            totals[day] = totals.GetValueOrDefault(day) + doc.Amount;
        }

        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(totals, nowUnix);
        return await graphBuilder.BuildInlineAsync(spec, dark, $"stars-revenue:{peerId}:{currency}", nowUnix);
    }
}
