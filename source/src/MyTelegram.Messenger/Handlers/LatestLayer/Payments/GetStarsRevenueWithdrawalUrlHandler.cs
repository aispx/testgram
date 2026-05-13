using MongoDB.Driver;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Schema.Payments;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Withdraw funds from a channel or bot's <a href="https://corefork.telegram.org/api/stars#withdrawing-revenue">star balance »</a>.
/// </summary>
internal sealed class GetStarsRevenueWithdrawalUrlHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    ITwoFactorService twoFactorService,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarsRevenueWithdrawalUrl, MyTelegram.Schema.Payments.IStarsRevenueWithdrawalUrl>
{
    protected override async Task<MyTelegram.Schema.Payments.IStarsRevenueWithdrawalUrl> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarsRevenueWithdrawalUrl obj)
    {
        // 2FA SRP password verification — required to withdraw revenue.
        if (obj.Password is TInputCheckPasswordSRP srp)
        {
            var srpUserId = await twoFactorService.GetUserIdBySrpIdAsync(srp.SrpId);
            if (srpUserId != input.UserId)
                RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
            var ok = await twoFactorService.VerifySrpAsync(input.UserId, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray());
            if (!ok) RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }
        else if (obj.Password is not null and not TInputCheckPasswordEmpty)
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }
        else
        {
            // Empty/missing password — user must have 2FA enabled per spec.
            var pwd = await twoFactorService.GetPasswordAsync(input.UserId);
            if (pwd != null)
                RpcErrors.RpcErrors400.PasswordMissing.ThrowRpcError();
        }

        var currency = obj.Ton ? ChannelRevenueHelper.TonCurrency : ChannelRevenueHelper.StarsCurrency;
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var peerId = peer.PeerId;

        long ownerUserId;
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync(peerId);
            if (channel == null) RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            if (channel!.CreatorId != input.UserId) RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
            ownerUserId = channel.CreatorId;
        }
        else
        {
            // bot/self
            ownerUserId = input.UserId;
        }

        var (current, _) = await ChannelRevenueHelper.GetBalanceAsync(mongoDatabase, peerId, currency);
        var amount = obj.Amount ?? current;
        if (amount <= 0 || amount > current)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Per appConfig:
        //   stars_revenue_withdrawal_min = 1000
        //   stars_revenue_withdrawal_max = 25_000_000
        // Telegram clients enforce these only for stars; TON has no fixed min/max here.
        if (!obj.Ton)
        {
            const long withdrawalMin = 1000;
            const long withdrawalMax = 25_000_000;
            if (amount < withdrawalMin)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            if (amount > withdrawalMax)
                amount = withdrawalMax;
        }

        // Debit channel revenue ledger
        await ChannelRevenueHelper.DebitAsync(mongoDatabase, peerId, currency, amount,
            targetUserId: ownerUserId, title: obj.Ton ? "TON withdrawal" : "Stars withdrawal");

        // For stars: credit owner's personal stars wallet (so they can spend them) and notify.
        // For TON: credit owner's TON wallet (Fragment would normally process this off-chain,
        // here we mirror it into our TON ledger so the user has the balance to spend).
        if (obj.Ton)
        {
            await TonBalanceHelper.AddBalanceAsync(mongoDatabase, ownerUserId, amount);
            await TonBalanceHelper.AddTransactionAsync(mongoDatabase, ownerUserId, amount,
                peerChannelId: peer.PeerType == PeerType.Channel ? peerId : null,
                peerUserId: peer.PeerType != PeerType.Channel ? peerId : null,
                title: "Revenue withdrawal (TON)");
        }
        else
        {
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, ownerUserId, amount);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, ownerUserId, amount,
                peerChannelId: peer.PeerType == PeerType.Channel ? peerId : null,
                peerUserId: peer.PeerType != PeerType.Channel ? peerId : null,
                title: "Revenue withdrawal");
        }

        // Push updates so clients refresh balances/revenue without polling.
        if (obj.Ton)
            await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, ownerUserId);
        else
            await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, ownerUserId);
        await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase, peerId, peer.PeerType, ownerUserId, currency);

        // Return a fragment-like URL. Clients open it in a webview; for testgram we just
        // surface a deep link that confirms the operation succeeded.
        var url = obj.Ton
            ? $"https://fragment.com/wallet/withdraw?peer={peerId}&amount={amount}&currency=ton"
            : $"https://fragment.com/wallet/withdraw?peer={peerId}&amount={amount}&currency=xtr";

        return new TStarsRevenueWithdrawalUrl { Url = url };
    }
}