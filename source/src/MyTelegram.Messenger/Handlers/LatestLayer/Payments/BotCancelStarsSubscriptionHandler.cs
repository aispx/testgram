using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Cancel a <a href="https://corefork.telegram.org/api/subscriptions#bot-subscriptions">bot subscription</a>
/// Possible errors
/// Code Type Description
/// 400 CHARGE_ID_INVALID The specified charge_id is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.botCancelStarsSubscription"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class BotCancelStarsSubscriptionHandler(
    IPeerHelper peerHelper,
    IStarsSubscriptionService starsSubscriptionService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestBotCancelStarsSubscription, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestBotCancelStarsSubscription obj)
    {
        if (string.IsNullOrEmpty(obj.ChargeId))
        {
            RpcErrors.RpcErrors400.ChargeIdInvalid.ThrowRpcError();
        }

        var subscriberPeer = peerHelper.GetPeer(obj.UserId, input.UserId);
        if (subscriberPeer == null || subscriberPeer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Scoped to the calling bot, so one bot cannot end another bot's subscription.
        var subscription = await starsSubscriptionService.GetBotSubscriptionByChargeAsync(
            input.UserId, subscriberPeer!.PeerId, obj.ChargeId);

        if (subscription == null)
        {
            RpcErrors.RpcErrors400.ChargeIdInvalid.ThrowRpcError();
        }

        // `restore` un-cancels; the paid-for period keeps running either way until until_date.
        await starsSubscriptionService.SetBotCanceledAsync(subscription!.Id, !obj.Restore);

        return new TBoolTrue();
    }
}