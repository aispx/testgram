using MongoDB.Driver;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Pushes <c>updateStarsBalance</c> / <c>updateStarsRevenueStatus</c> to clients in realtime.
/// Mirrors the behaviour of upstream Telegram, where any balance change is followed by a
/// matching update so the client UI refreshes without re-polling <c>payments.getStarsStatus</c>.
/// </summary>
public static class BalancePushHelper
{
    /// <summary>Push the current stars balance of a user to all their sessions.</summary>
    public static async Task PushStarsBalanceAsync(IObjectMessageSender sender, IMongoDatabase db, long userId)
    {
        try
        {
            var balance = await StarsBalanceHelper.GetBalanceAsync(db, userId);
            await PushBalanceAsync(sender, userId, new TStarsAmount { Amount = balance });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Push the current TON balance of a user to all their sessions.</summary>
    public static async Task PushTonBalanceAsync(IObjectMessageSender sender, IMongoDatabase db, long userId)
    {
        try
        {
            var balance = await TonBalanceHelper.GetBalanceAsync(db, userId);
            await PushBalanceAsync(sender, userId, new TStarsTonAmount { Amount = balance });
        }
        catch { /* best-effort */ }
    }

    private static Task PushBalanceAsync(IObjectMessageSender sender, long userId, IStarsAmount balance)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateStarsBalance { Balance = balance }
            },
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
        return sender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates);
    }

    /// <summary>
    /// Push <c>updateStarsRevenueStatus</c> for a channel/bot peer to the owner.
    /// </summary>
    public static async Task PushChannelRevenueStatusAsync(IObjectMessageSender sender, IMongoDatabase db,
        long peerId, PeerType peerType, long ownerUserId, string currency)
    {
        try
        {
            var (current, overall) = await ChannelRevenueHelper.GetBalanceAsync(db, peerId, currency);
            var status = new TStarsRevenueStatus
            {
                CurrentBalance = ChannelRevenueHelper.BuildAmount(currency, current),
                AvailableBalance = ChannelRevenueHelper.BuildAmount(currency, current),
                OverallRevenue = ChannelRevenueHelper.BuildAmount(currency, overall),
                WithdrawalEnabled = current > 0 && (currency == ChannelRevenueHelper.TonCurrency || current >= 1000),
            };
            IPeer peerTl = peerType == PeerType.Channel
                ? new TPeerChannel { ChannelId = peerId }
                : new TPeerUser { UserId = peerId };
            var updates = new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new TUpdateStarsRevenueStatus { Peer = peerTl, Status = status }
                },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await sender.PushMessageToPeerAsync(new Peer(PeerType.User, ownerUserId), updates);
        }
        catch { /* best-effort */ }
    }
}
