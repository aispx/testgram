using MyTelegram.Domain.Aggregates.ChatInvite;
using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Re-join a private channel associated to an active <a href="https://corefork.telegram.org/api/invites#paid-invite-links">Telegram Star subscription »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.fulfillStarsSubscription"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class FulfillStarsSubscriptionHandler(
    IStarsSubscriptionService starsSubscriptionService,
    IQueryProcessor queryProcessor,
    ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestFulfillStarsSubscription, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestFulfillStarsSubscription obj)
    {
        if (string.IsNullOrEmpty(obj.SubscriptionId))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Scoped to the caller's own user id so another user's subscription cannot be fulfilled.
        var subscription = await starsSubscriptionService.GetSubscriptionByIdAsync(input.UserId, obj.SubscriptionId);
        if (subscription == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // Re-joining is free only while the already paid-for period is still running; once it has
        // lapsed the user has to buy a new one through payments.sendStarsForm.
        if (subscription!.UntilDate <= CurrentDate)
        {
            RpcErrors.RpcErrors400.StarsPaymentRequired.ThrowRpcError();
        }

        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(subscription.PeerId, input.UserId));
        if (channelMember is { Left: false, Kicked: false })
        {
            RpcErrors.RpcErrors400.UserAlreadyParticipant.ThrowRpcError();
        }

        if (channelMember is { Kicked: true })
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(subscription.InviteHash));
        if (chatInviteReadModel == null || chatInviteReadModel.Revoked)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var command = new ImportChatInviteCommand(
            ChatInviteId.Create(chatInviteReadModel!.PeerId, chatInviteReadModel.InviteId),
            input.ToRequestInfo(),
            ChatInviteRequestState.NoApprovalRequired,
            CurrentDate,
            subscription.UntilDate);
        await commandBus.PublishAsync(command);

        return new TBoolTrue();
    }
}
