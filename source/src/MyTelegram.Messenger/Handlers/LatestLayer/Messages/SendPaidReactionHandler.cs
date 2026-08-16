using EventFlow.Exceptions;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Extensions;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Sends one or more <a href="https://corefork.telegram.org/api/reactions#paid-reactions">paid Telegram Star reactions</a>.
/// Possible errors
/// Code Type Description
/// 400 BALANCE_TOO_LOW The current balance is too low.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 REACTIONS_COUNT_INVALID The specified reactions count is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendPaidReaction"/> </c></para>
/// </summary>
internal sealed class SendPaidReactionHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMongoDatabase mongoDatabase,
    IAppConfigHelper appConfigHelper,
    IPaidReactionPrivacyAppService paidReactionPrivacyAppService,
    IChannelAppService channelAppService,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendPaidReaction, MyTelegram.Schema.IUpdates>
{
    private const string SentCollectionName = "paid_reaction_sent";

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendPaidReaction obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        if (obj.Count <= 0)
            RpcErrors.RpcErrors400.ReactionsCountInvalid.ThrowRpcError();

        var amountMax = appConfigHelper.GetInt32Value("stars_paid_reaction_amount_max", 2500);
        if (obj.Count > amountMax)
            RpcErrors.RpcErrors400.ReactionsCountInvalid.ThrowRpcError();

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer.PeerId));
        if (channelReadModel == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        // A paid reaction spends stars into the channel and shows on the post, so it requires
        // membership; GetPeer validates no access hash.
        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
            return null!;

        if (!channelReadModel!.PaidReactionsEnabled)
            RpcErrors.RpcErrors400.ReactionsCountInvalid.ThrowRpcError();

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(peer.PeerId, obj.MsgId)) as MessageReadModel;
        if (messageReadModel == null)
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();

        // random_id makes the request idempotent: a retried or double-tapped send must not charge twice.
        if (!await TryClaimRandomIdAsync(input.UserId, obj.RandomId))
        {
            return null!;
        }

        // Atomic conditional debit — the real balance guard. A separate read-then-write check races:
        // two concurrent sends could both pass it and both debit, driving the balance negative and
        // minting stars into the channel owner's wallet. Done before any other writes so an
        // insufficient balance fails fast with no side effects.
        if (!await StarsBalanceHelper.TryDebitAsync(mongoDatabase, input.UserId, obj.Count))
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        var setting = obj.Private != null
            ? PaidReactionPrivacyConverter.FromTl(obj.Private, peerHelper, input.UserId)
            : await paidReactionPrivacyAppService.GetDefaultAsync(input.UserId);

        // A paid reaction attributed to a channel shows that channel as the donor on the top-reactors
        // leaderboard. FromTl accepts any channel id without proof, so verify the caller can actually
        // post as the named channel; otherwise fall back to the default attribution. Without this, a
        // one-star reaction could impersonate any channel on the server.
        if (setting.Type == PaidReactionPrivacyType.Peer && setting.PeerId != 0)
        {
            var sendAsPeer = new Peer(PeerType.Channel, setting.PeerId);
            if (!await messageAppService.IsValidSendAsPeerAsync(input.UserId, peer, sendAsPeer))
            {
                setting = new PaidReactionPrivacySetting(PaidReactionPrivacyType.Default);
            }
        }

        await paidReactionPrivacyAppService.SetForMessageAsync(input.UserId, peer.PeerId, obj.MsgId, setting);

        // Credit the channel owner only after the debit succeeded.
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, channelReadModel.CreatorId, obj.Count);
        // Tag both legs with reaction:true so starsTransaction renders the
        // "paid reaction" label on both sender and receiver wallets.
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -obj.Count, peerChannelId: peer.PeerId, reaction: true, msgId: obj.MsgId);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, channelReadModel.CreatorId, obj.Count, peerChannelId: peer.PeerId, reaction: true, msgId: obj.MsgId);

        var anonymous = setting.Type != PaidReactionPrivacyType.Default;
        var anonymousPeerId = setting.Type == PaidReactionPrivacyType.Peer ? setting.PeerId : 0;

        // Only this user's reactions are carried; the aggregate keeps everyone else's from its
        // authoritative state so a concurrent reaction is never clobbered.
        // Keep our own non-paid reaction: sendPaidReaction must not clear a regular reaction.
        var ownReactions = messageReadModel!.RecentReactions2?
            .Where(r => r.SenderUserId == input.UserId && r.Reaction is not TReactionPaid)
            .Select(r => new Reaction(
                r.SenderUserId,
                r.Reaction is TReactionEmoji e ? e.Emoticon : null,
                r.Reaction is TReactionCustomEmoji c ? c.DocumentId : (long?)null,
                r.Date))
            .ToList() ?? [];

        // Paid reactions accumulate: previously sent stars stay, this request adds obj.Count more.
        var previousOwnPaidCount = messageReadModel.RecentReactions2?
            .Count(r => r.SenderUserId == input.UserId && r.Reaction is TReactionPaid) ?? 0;

        for (var i = 0; i < previousOwnPaidCount + obj.Count; i++)
        {
            ownReactions.Add(new Reaction(input.UserId, null, null, CurrentDate, IsPaid: true,
                Anonymous: anonymous, AnonymousPeerId: anonymousPeerId));
        }

        var messageId = MessageId.Create(peer.PeerId, obj.MsgId);
        try
        {
            await commandBus.PublishAsync(new UpdateMessageReactionsCommand(messageId, input.ToRequestInfo(), input.UserId, ownReactions));
        }
        catch (DomainError)
        {
            // Message exists in the read model but its aggregate was never created (legacy message).
        }

        return null!;
    }

    /// <summary>
    /// Inserts the random_id, returning false when this send was already processed.
    /// </summary>
    private async Task<bool> TryClaimRandomIdAsync(long userId, long randomId)
    {
        if (randomId == 0)
        {
            // Clients are expected to supply one; without it there is nothing to deduplicate on.
            return true;
        }

        try
        {
            await mongoDatabase.GetCollection<BsonDocument>(SentCollectionName).InsertOneAsync(new BsonDocument
            {
                ["_id"] = $"paid-reaction-{userId}-{randomId}",
                ["UserId"] = userId,
                ["RandomId"] = randomId,
                ["Date"] = CurrentDate
            });
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}
