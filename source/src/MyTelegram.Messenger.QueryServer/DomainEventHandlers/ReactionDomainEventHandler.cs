using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class ReactionDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper,
    IChannelAppService channelAppService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageReactionsUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageReactionsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var messageItem = e.MessageItem;
        var reactions = e.Reactions;

        var userReactions = reactions
            .Where(r => r.UserId == e.RequestInfo.UserId)
            .Select((r, i) => (r.GetReactionId(), Order: i))
            .ToDictionary(x => x.Item1, x => x.Order);

        var reactionCounts = reactions
            .GroupBy(r => r.GetReactionId())
            .Select(g =>
            {
                var first = g.First();
                userReactions.TryGetValue(g.Key, out var order);
                return new TReactionCount
                {
                    Reaction = first.IsPaid ? new TReactionPaid()
                        : string.IsNullOrEmpty(first.Emoticon)
                        ? new TReactionCustomEmoji { DocumentId = first.CustomEmojiDocumentId!.Value }
                        : (IReaction)new TReactionEmoji { Emoticon = first.Emoticon },
                    Count = first.IsPaid ? g.Sum(r => r.Big ? 1 : 1) : g.Count(),
                    ChosenOrder = userReactions.ContainsKey(g.Key) ? order : (int?)null
                };
            })
            .Cast<IReactionCount>()
            .ToList();

        var isChannel = messageItem.OwnerPeer.PeerType == PeerType.Channel && messageItem.ToPeer.PeerType == PeerType.Channel;
        if (isChannel)
        {
            var channel = await channelAppService.GetAsync(messageItem.OwnerPeer.PeerId);
            isChannel = channel?.Broadcast ?? false;
        }

        var recentReactions = isChannel ? [] : reactions.Select(r => new TMessagePeerReaction
        {
            PeerId = new TPeerUser { UserId = r.UserId },
            Date = r.Date ?? 0,
            Big = r.Big,
            My = r.UserId == e.RequestInfo.UserId,
            Unread = r.UserId != e.RequestInfo.UserId && messageItem.SenderUserId == e.RequestInfo.UserId,
            Reaction = string.IsNullOrEmpty(r.Emoticon)
                ? new TReactionCustomEmoji { DocumentId = r.CustomEmojiDocumentId!.Value }
                : new TReactionEmoji { Emoticon = r.Emoticon }
        }).Cast<IMessagePeerReaction>().ToList();

        var messageReactions = new TMessageReactions
        {
            Results = new TVector<IReactionCount>(reactionCounts),
            RecentReactions = recentReactions.Count > 0 ? new TVector<IMessagePeerReaction>(recentReactions) : null,
            CanSeeList = !isChannel
        };

        var ownerPeer = messageItem.OwnerPeer;
        var pts = await ptsHelper.IncrementPtsAsync(ownerPeer.PeerId, 1);
        
        var senderPeer = messageItem.SenderUserId == 0 ? ownerPeer : new Peer(PeerType.User, messageItem.SenderUserId);

        var updateReactions = new TUpdateMessageReactions
        {
            Peer = senderPeer.ToPeer(),
            MsgId = messageItem.MessageId,
            Reactions = messageReactions
        };

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(updateReactions),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };

        await SendRpcMessageToClientAsync(e.RequestInfo, updates, ownerPeer.PeerId, pts, ownerPeer.PeerType);

        var toPeer = messageItem.ToPeer;
        if (toPeer.PeerType == PeerType.User)
        {
            // For outbox PM messages (Out=true), the inbox event already handles both sides.
            // Only push to the other party from the inbox event (Out=false) to avoid duplicates.
            if (messageItem.IsOut)
                return;

            var otherUserId = messageItem.SenderUserId == e.RequestInfo.UserId
                ? toPeer.PeerId
                : messageItem.SenderUserId;

            // Get the outbox MessageId at the sender's side
            var inboxReadModel = await queryProcessor.ProcessAsync(
                new GetMessageByPeerIdAndMessageIdQuery(ownerPeer.PeerId, messageItem.MessageId)) as IMessageReadModel;
            var outboxMsgId = inboxReadModel?.SenderMessageId > 0
                ? inboxReadModel.SenderMessageId
                : messageItem.MessageId;

            // The other user (sender of the original message) sees peer = the reactor (ownerPeer)
            var otherPeer = new Peer(PeerType.User, otherUserId);
            var otherUpdate = new TUpdateMessageReactions
            {
                Peer = ownerPeer.ToPeer(),
                MsgId = outboxMsgId,
                Reactions = messageReactions
            };
            var otherUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(otherUpdate),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await PushUpdatesToPeerAsync(otherPeer, otherUpdates,
                excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
                pts: pts);
        }
        else if (toPeer.PeerType == PeerType.Channel || toPeer.PeerType == PeerType.Chat)
        {
            var channelUpdate = new TUpdateMessageReactions
            {
                Peer = toPeer.ToPeer(),
                MsgId = messageItem.MessageId,
                Reactions = messageReactions
            };
            var channelUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(channelUpdate),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await PushUpdatesToPeerAsync(toPeer, channelUpdates,
                excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
                pts: pts);
        }
    }
}
