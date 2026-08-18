using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Discussion;

/// <summary>
/// Delivers replies to a comment to a user who never joined the discussion group, through the
/// <c>@replies</c> peer.
/// <para>
/// A user may comment on a channel post without joining the linked supergroup, so they receive no
/// updates from it and would never learn that somebody answered them. Telegram solves this by
/// mirroring the reply into a private chat with the <c>@replies</c> peer, carrying the discussion
/// group and the answered message in its <c>fwd_from</c>/<c>reply_to</c> headers so clients can offer
/// a "View in chat" button.
/// </para>
/// See https://corefork.telegram.org/api/discussion#replies
/// </summary>
public class RepliesNotificationSubscriber(
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IMessageAppService messageAppService,
    IRepliesBlockService repliesBlockService,
    ILogger<RepliesNotificationSubscriber> logger)
    : ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent>
{
    public async Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        foreach (var item in domainEvent.AggregateEvent.MessageItems)
        {
            try
            {
                await TryNotifyAsync(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A missed @replies mirror must never fail the send it is derived from.
                logger.LogWarning(ex,
                    "Failed to deliver an @replies notification for message {MessageId} in {PeerId}",
                    item.MessageId,
                    item.ToPeer.PeerId);
            }
        }
    }

    private async Task TryNotifyAsync(MessageItem item)
    {
        if (item.ToPeer.PeerType != PeerType.Channel ||
            item.SendMessageType == SendMessageType.MessageService ||
            item.InputReplyTo is not TInputReplyToMessage { ReplyToMsgId: > 0 } inputReplyToMessage)
        {
            return;
        }

        var groupId = item.ToPeer.PeerId;
        var channelReadModel = await channelAppService.GetAsync((long?)groupId);

        // Only the comment section of a channel has guests: everywhere else the sender of the
        // answered message is a member and gets the ordinary channel update.
        if (channelReadModel is not { Broadcast: false, LinkedChatId: not null })
        {
            return;
        }

        var repliedTo = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(groupId, inputReplyToMessage.ReplyToMsgId).Value));
        if (repliedTo == null)
        {
            return;
        }

        var authorUserId = repliedTo.SenderUserId;
        if (authorUserId <= 0 ||
            authorUserId == item.SenderUserId ||
            PeerKindHelper.IsSystemUserId(authorUserId))
        {
            return;
        }

        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(groupId, authorUserId));
        if (channelMember is { Left: false, Kicked: false })
        {
            return;
        }

        if (await repliesBlockService.IsBlockedAsync(authorUserId, item.SenderUserId))
        {
            return;
        }

        var replierPeer = item.SendAs ?? new Peer(PeerType.User, item.SenderUserId);
        var threadRootId = item.TopMsgId ?? inputReplyToMessage.TopMsgId ?? inputReplyToMessage.ReplyToMsgId;

        var fwdHeader = new MessageFwdHeader
        {
            FromId = replierPeer,
            SavedFromPeer = item.ToPeer,
            SavedFromMsgId = item.MessageId,
            Date = item.Date
        };

        // reply_to_peer_id points back into the discussion group, so the client can open the thread
        // from the @replies chat.
        var replyTo = new TInputReplyToMessage
        {
            ReplyToMsgId = inputReplyToMessage.ReplyToMsgId,
            TopMsgId = threadRootId,
            ReplyToPeerId = new TInputPeerChannel { ChannelId = groupId, AccessHash = 0 }
        };

        var sendInput = new SendMessageInput(
            RequestInfo.Empty with { UserId = MyTelegramConsts.RepliesServiceUserId, RequestId = Guid.NewGuid() },
            MyTelegramConsts.RepliesServiceUserId,
            new Peer(PeerType.User, authorUserId),
            item.Message,
            Random.Shared.NextInt64(),
            entities: item.Entities,
            inputReplyTo: replyTo,
            media: item.Media,
            fwdHeader: fwdHeader);

        await messageAppService.SendMessageAsync([sendInput]);
    }
}
