using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Activate or deactivate a <a href="https://corefork.telegram.org/api/invites#paid-invite-links">Telegram Star subscription »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.changeStarsSubscription"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ChangeStarsSubscriptionHandler(IStarsSubscriptionService starsSubscriptionService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestChangeStarsSubscription, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestChangeStarsSubscription obj)
    {
        if (string.IsNullOrEmpty(obj.SubscriptionId))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Looked up by the caller's own user id, so one user cannot cancel another user's
        // subscription by guessing its id.
        var subscription = await starsSubscriptionService.GetSubscriptionByIdAsync(input.UserId, obj.SubscriptionId);
        if (subscription == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Cancelling only stops the renewal; the paid-for period keeps running until until_date.
        if (obj.Canceled.HasValue)
        {
            await starsSubscriptionService.SetCanceledAsync(input.UserId, obj.SubscriptionId, obj.Canceled.Value);
        }

        return new TBoolTrue();
    }
}
