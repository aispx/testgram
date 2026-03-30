using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class SendPaidReactionHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendPaidReaction, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendPaidReaction obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        if (obj.Count <= 0)
            RpcErrors.RpcErrors400.ReactionsCountInvalid.ThrowRpcError();

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer.PeerId));
        if (channelReadModel == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (!channelReadModel!.PaidReactionsEnabled)
            RpcErrors.RpcErrors400.ReactionsCountInvalid.ThrowRpcError();

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < obj.Count)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(peer.PeerId, obj.MsgId)) as MessageReadModel;
        if (messageReadModel == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        // Deduct stars from sender, add to channel owner
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -obj.Count);
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, channelReadModel.CreatorId, obj.Count);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -obj.Count, peerChannelId: peer.PeerId);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, channelReadModel.CreatorId, obj.Count, peerChannelId: peer.PeerId);

        // Build new reactions list: keep existing non-paid reactions + add count paid reactions for this user
        var existingReactions = messageReadModel!.RecentReactions2?
            .Where(r => r.Reaction is not TReactionPaid || r.SenderUserId != input.UserId)
            .Select(r => new Reaction(
                r.SenderUserId,
                r.Reaction is TReactionEmoji e ? e.Emoticon : null,
                r.Reaction is TReactionCustomEmoji c ? c.DocumentId : (long?)null,
                r.Date,
                IsPaid: r.Reaction is TReactionPaid))
            .ToList() ?? [];

        // Add obj.Count paid reactions for this user
        for (var i = 0; i < obj.Count; i++)
            existingReactions.Add(new Reaction(input.UserId, null, null, CurrentDate, IsPaid: true));

        var messageId = MessageId.Create(peer.PeerId, obj.MsgId);
        try
        {
            await commandBus.PublishAsync(new UpdateMessageReactionsCommand(messageId, input.ToRequestInfo(), existingReactions));
        }
        catch (Exception ex) when (ex.Message.Contains("AggregateIsCreatedSpecification")) { }

        return null!;
    }
}
