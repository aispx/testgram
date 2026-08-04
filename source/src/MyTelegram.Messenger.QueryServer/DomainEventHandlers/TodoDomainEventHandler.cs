using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Pushes <a href="https://corefork.telegram.org/api/todo">todo list »</a> changes (appended items,
/// toggled completions) to every participant as an edit of the message carrying the checklist.
/// </summary>
/// <remarks>
/// A checklist lives inside a single message, but each side of a private chat owns its own copy of
/// that message with its own id. The request handlers publish <c>UpdateTodoListCommand</c> to every
/// copy, so this handler only has to notify the owner of the copy it was raised for — which keeps
/// the message ids correct without resolving them here.
/// </remarks>
public class TodoDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IPtsHelper ptsHelper,
    IMessageConverterService messageConverterService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageTodoUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageTodoUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var oldMessageItem = e.MessageItem;
        var ownerPeer = oldMessageItem.OwnerPeer;

        var pts = await ptsHelper.IncrementPtsAsync(ownerPeer.PeerId, 1);
        var newMessageItem = oldMessageItem with
        {
            Media = TodoMediaFactory.Create(e.Todo, e.Completions),
            Pts = pts
        };

        // The owner of this copy sees the message from their own point of view.
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

        // Private chat: the command is published to both copies of the message, and each copy
        // notifies its own owner. Pushing to the other party here as well would duplicate updates.
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
