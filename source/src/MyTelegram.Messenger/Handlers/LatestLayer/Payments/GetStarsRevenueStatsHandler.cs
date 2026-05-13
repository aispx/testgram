using MyTelegram.Schema.Payments;

using System.Text.Json;
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
internal sealed class GetStarsRevenueStatsHandler(IMongoDatabase mongoDatabase, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsRevenueStats, MyTelegram.Schema.Payments.IStarsRevenueStats>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsRevenueStats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsRevenueStats obj)
    {
        var currency = obj.Ton ? ChannelRevenueHelper.TonCurrency : ChannelRevenueHelper.StarsCurrency;

        // Resolve target peer: inputPeerSelf = bot owner (own balance), otherwise a channel/bot
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        long peerId = peer.PeerType switch
        {
            PeerType.Channel => peer.PeerId,
            PeerType.User or PeerType.Self => peer.PeerId, // bots (self) — same ledger keyed by user id
            _ => 0
        };

        var (current, overall) = await ChannelRevenueHelper.GetBalanceAsync(mongoDatabase, peerId, currency);

        // Build a simple last-30-days revenue graph from transactions
        var graph = await BuildRevenueGraphAsync(peerId, currency);

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
            UsdRate = obj.Ton ? 3.5293105 : 0.0141,
        };
    }

    private async Task<IStatsGraph> BuildRevenueGraphAsync(long peerId, string currency)
    {
        var since = DateTime.UtcNow.AddDays(-30).ToTimestamp();
        var filter = Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.PeerId, peerId)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Eq(x => x.Currency, currency)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Gte(x => x.Date, since)
                     & Builders<ChannelRevenueTransactionDocument>.Filter.Gt(x => x.Amount, 0L);
        var docs = await mongoDatabase.GetCollection<ChannelRevenueTransactionDocument>(ChannelRevenueHelper.TransactionCollection)
            .Find(filter).ToListAsync();

        if (docs.Count == 0)
        {
            return new TStatsGraph
            {
                Json = new TDataJSON { Data = "{\"columns\":[[\"x\"],[\"y0\"]],\"types\":{\"y0\":\"line\",\"x\":\"x\"},\"names\":{\"y0\":\"Revenue\"}}" }
            };
        }

        // Group by day (UTC midnight)
        var byDay = docs.GroupBy(d => (long)d.Date - (d.Date % 86400))
            .OrderBy(g => g.Key)
            .ToList();
        var xs = new List<long> { };
        var ys = new List<long> { };
        foreach (var g in byDay)
        {
            xs.Add(g.Key * 1000L);
            ys.Add(g.Sum(x => x.Amount));
        }

        var dataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            columns = new object[]
            {
                new object[] { "x" }.Concat(xs.Cast<object>()).ToArray(),
                new object[] { "y0" }.Concat(ys.Cast<object>()).ToArray(),
            },
            types = new Dictionary<string, string> { ["y0"] = "line", ["x"] = "x" },
            names = new Dictionary<string, string> { ["y0"] = "Revenue" },
        });
        return new TStatsGraph { Json = new TDataJSON { Data = dataJson } };
    }
}