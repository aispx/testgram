using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// React to message.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendReaction"/> </c></para>
/// </summary>
internal sealed class SendReactionHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendReaction, MyTelegram.Schema.IUpdates>
{
    private const string RecentKey = "recent_reactions";

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendReaction obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // For private chats, OwnerPeerId is the current user
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, obj.MsgId)) as MessageReadModel;
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var otherReactions = messageReadModel.RecentReactions2?
            .Where(r => r.SenderUserId != input.UserId)
            .Select(r => new Reaction(r.SenderUserId, r.Reaction is TReactionEmoji e ? e.Emoticon : null,
                r.Reaction is TReactionCustomEmoji c ? c.DocumentId : null, r.Date,
                IsPaid: r.Reaction is TReactionPaid))
            .ToList() ?? [];

        var newReactions = new List<Reaction>(otherReactions);
        if (obj.Reaction != null)
        {
            foreach (var reaction in obj.Reaction)
            {
                switch (reaction)
                {
                    case TReactionEmoji emoji:
                        newReactions.Add(new Reaction(input.UserId, emoji.Emoticon, null, CurrentDate, obj.Big));
                        break;
                    case TReactionCustomEmoji custom:
                        newReactions.Add(new Reaction(input.UserId, null, custom.DocumentId, CurrentDate, obj.Big));
                        break;
                }
            }
        }

        var messageId = MessageId.Create(ownerPeerId, obj.MsgId);
        try
        {
            await commandBus.PublishAsync(new UpdateMessageReactionsCommand(messageId, input.ToRequestInfo(), newReactions));
        }
        catch (Exception ex) when (ex.Message.Contains("AggregateIsCreatedSpecification"))
        {
            // Message exists in ReadModel but aggregate not created (old message) - ignore
        }

        // For PM inbox messages, also update the outbox copy at the sender's side
        if (peer.PeerType == PeerType.User && !messageReadModel.Out && messageReadModel.SenderMessageId > 0)
        {
            var outboxMessageId = MessageId.Create(messageReadModel.SenderUserId, messageReadModel.SenderMessageId);
            try
            {
                await commandBus.PublishAsync(new UpdateMessageReactionsCommand(outboxMessageId, input.ToRequestInfo(), newReactions));
            }
            catch (Exception ex) when (ex.Message.Contains("AggregateIsCreatedSpecification")) { }
        }

        // Update recent reactions list if add_to_recent flag is set
        if (obj.AddToRecent && obj.Reaction?.Count > 0)
        {
            var recentConfig = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));
            var existing = recentConfig?.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

            foreach (var reaction in obj.Reaction)
            {
                if (reaction is not TReactionEmoji emoji) continue;
                existing.Remove(emoji.Emoticon);
                existing.Insert(0, emoji.Emoticon);
            }

            // Keep max 20
            if (existing.Count > 20) existing = existing.Take(20).ToList();

            var configId = UserConfigId.Create(input.UserId, RecentKey);
            await commandBus.PublishAsync(new UpdateUserConfigCommand(configId, input.ToRequestInfo(), input.UserId, RecentKey, string.Join(',', existing)));
        }

        return null!;
    }
}
