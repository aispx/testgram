using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Pushes the receipt link recorded on a paid invoice to every participant as an edit of the invoice
/// message.
/// </summary>
/// <remarks>
/// <para>
/// Clients read <c>messageMediaInvoice.receipt_msg_id</c> as <c>messageInvoice.receipt_message_id</c>
/// and swap the bubble's <em>Pay</em> button for <em>Receipt</em>; without this push the buyer would
/// keep seeing a payable invoice until the chat is refetched.
/// </para>
/// <para>
/// <c>edit_date</c> is deliberately left untouched — this is a server side state change, not the
/// author editing their message, so no client labels the invoice "edited".
/// </para>
/// <para>
/// Each side of a private chat owns its own copy of the invoice message.
/// <c>InvoiceReceiptPublisher</c> publishes the command to every copy, so this handler only notifies
/// the owner of the copy it was raised for — which keeps the message ids correct without resolving
/// them here. Same arrangement as <c>TodoDomainEventHandler</c>.
/// </para>
/// <para>See https://corefork.telegram.org/api/payments#5-checkout </para>
/// </remarks>
public class InvoiceReceiptDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IPtsHelper ptsHelper,
    IMessageConverterService messageConverterService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageInvoiceReceiptUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageInvoiceReceiptUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var oldMessageItem = e.MessageItem;
        var updatedMedia = InvoiceMediaFactory.WithReceipt(oldMessageItem.Media, e.ReceiptMsgId);
        if (ReferenceEquals(updatedMedia, oldMessageItem.Media))
        {
            // Not an invoice, or the receipt was already recorded: nothing changed to push.
            return;
        }

        var ownerPeer = oldMessageItem.OwnerPeer;
        var pts = await ptsHelper.IncrementPtsAsync(ownerPeer.PeerId, 1);
        var newMessageItem = oldMessageItem with
        {
            Media = updatedMedia,
            Pts = pts
        };

        await SendRpcMessageToClientAsync(
            e.RequestInfo,
            ToEditUpdates(ownerPeer.PeerId, newMessageItem, pts),
            ownerPeer.PeerId,
            pts,
            newMessageItem.ToPeer.PeerType);

        var toPeer = newMessageItem.ToPeer;
        if (toPeer.PeerType is PeerType.Channel or PeerType.Chat)
        {
            // Channels and groups share a single message, so one push to the peer covers everyone.
            await PushUpdatesToPeerAsync(
                toPeer,
                ToEditUpdates(0, newMessageItem, pts),
                excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
                pts: pts);
            return;
        }

        await PushUpdatesToPeerAsync(
            ownerPeer,
            ToEditUpdates(ownerPeer.PeerId, newMessageItem, pts),
            excludeAuthKeyId: e.RequestInfo.PermAuthKeyId,
            pts: pts);
    }

    private IUpdates ToEditUpdates(long selfUserId, MessageItem messageItem, int pts)
    {
        IUpdate update = messageItem.ToPeer.PeerType == PeerType.Channel
            ? new TUpdateEditChannelMessage
            {
                Message = messageConverterService.ToMessage(selfUserId, messageItem),
                Pts = pts,
                PtsCount = 1
            }
            : new TUpdateEditMessage
            {
                Message = messageConverterService.ToMessage(selfUserId, messageItem),
                Pts = pts,
                PtsCount = 1
            };

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }
}
