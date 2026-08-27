using MyTelegram.Services.Extensions;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;


/// <summary>
/// Tells the user's other sessions that a <a href="https://corefork.telegram.org/api/drafts">draft</a>
/// changed: "New drafts are automatically sent to all devices via updateDraftMessage updates."
///
/// <para>Without this the only way a second device learns about a draft is asking for the whole list,
/// and clients do that at most once: Android persists <c>UserConfig.draftsLoaded</c> forever, so
/// <c>messages.getAllDrafts</c> is never called again for that account. The clearing half matters just
/// as much for TDLib based clients — <c>MessagesManager::clear_all_draft_messages</c> drops only secret
/// chat drafts locally and waits for <c>draftMessageEmpty</c> from the server for everything else.</para>
/// </summary>
public class DraftDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    ILayeredService<IDraftMessageConverter> draftMessageLayeredService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<DialogAggregate, DialogId, DraftSavedEvent>,
        ISubscribeSynchronousTo<DialogAggregate, DialogId, DraftClearedEvent>
{
    public Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, DraftSavedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;
        var draft = aggregateEvent.Draft;
        var draftMessage = draftMessageLayeredService.GetConverter(Layers.LayerLatest).ToDraftMessage(draft);

        return NotifyAsync(aggregateEvent.RequestInfo,
            aggregateEvent.OwnerPeerId,
            aggregateEvent.Peer,
            [new DraftTopic(draft.TopMsgId, draft.SavedPeerId)],
            _ => draftMessage);
    }

    public Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, DraftClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;

        return NotifyAsync(aggregateEvent.RequestInfo,
            aggregateEvent.OwnerPeerId,
            aggregateEvent.Peer,
            DraftTopicKey.OrChatLevel(aggregateEvent.Topics),
            _ => new TDraftMessageEmpty { Date = DateTime.UtcNow.ToTimestamp() });
    }

    private async Task NotifyAsync(RequestInfo requestInfo,
        long ownerPeerId,
        Peer? peer,
        IReadOnlyList<DraftTopic> topics,
        Func<DraftTopic, IDraftMessage> draftMessageFactory)
    {
        // Drafts cleared before the event carried its peer: there is nothing to address the update to.
        if (peer == null || ownerPeerId == 0)
        {
            return;
        }

        var schemaPeer = peer.ToPeer();
        var draftUpdates = topics.Select(IUpdate (topic) => new TUpdateDraftMessage
        {
            Peer = schemaPeer,
            TopMsgId = topic.TopMsgId,
            SavedPeerId = topic.SavedPeerId?.ToPeer(),
            Draft = draftMessageFactory(topic)
        });

        var updates = new TUpdates
        {
            Updates = [.. draftUpdates],
            Users = [.. await GetUsersAsync(peer)],
            Chats = [.. await GetChatsAsync(peer)],
            Date = DateTime.UtcNow.ToTimestamp()
        };

        // Not IObjectMessageSender directly: this goes through the base so the update is persisted with
        // a globalSeqNo and a session that was offline still gets it from updates.getDifference.
        // updateDraftMessage carries no pts, so there is no pts to advance.
        //
        // The originating session is excluded: it already applied the draft locally, and an echo back
        // rewrites the text the user is typing (Android posts newDraftReceived for it).
        await PushMessageToPeerAsync(new Peer(PeerType.User, ownerPeerId),
            updates,
            excludeAuthKeyId: requestInfo.PermAuthKeyId);
    }

    /// <summary>
    /// The peer of the draft has to travel with the update: TDLib repairs a draft for a dialog it does
    /// not know by calling <c>messages.getPeerDialogs</c>, but only when it has read access to the peer
    /// (<c>MessagesManager::on_update_dialog_draft_message</c>) — that is, only when it knows the user
    /// or the chat. Typing into a chat with no history is exactly that case.
    /// </summary>
    private async Task<List<ILayeredUser>> GetUsersAsync(Peer peer)
    {
        // PeerType.Self is the Saved Messages chat: still a user, and its own draft.
        return peer.PeerType is PeerType.User or PeerType.Self
            ? await userConverterService.GetUserListAsync(RequestInfo.Empty, [peer.PeerId])
            : [];
    }

    private async Task<List<IChat>> GetChatsAsync(Peer peer)
    {
        return peer.PeerType == PeerType.Channel
            ? await chatConverterService.GetChannelListAsync(RequestInfo.Empty, [peer.PeerId])
            : [];
    }
}
