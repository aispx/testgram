using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Services.Extensions;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class DialogDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IResponseCacheAppService responseCacheAppService,
    ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<DialogAggregate, DialogId, ChannelHistoryClearedEvent>,
        ISubscribeSynchronousTo<DialogAggregate, DialogId, DialogPinChangedEvent>,
        ISubscribeSynchronousTo<DialogFilterAggregate, DialogFilterId, DialogFilterUpdatedEvent>,
        ISubscribeSynchronousTo<DialogFilterAggregate, DialogFilterId, DialogFilterDeletedEvent>,
        ISubscribeSynchronousTo<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFiltersOrderUpdatedEvent>,
        ISubscribeSynchronousTo<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFilterTagsToggledEvent>,
        ISubscribeSynchronousTo<DialogFilterSettingsAggregate, DialogFilterSettingsId,
            DialogArchivePinnedUpdatedEvent>,
        ISubscribeSynchronousTo<EditPeerFoldersSaga, EditPeerFoldersSagaId, EditPeerFoldersCompletedSagaEvent>

{
    public async Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, ChannelHistoryClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo,
                new TBoolTrue())
     ;
    }

    public async Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, DialogPinChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo,
                new TBoolTrue(),
                domainEvent.AggregateEvent.OwnerPeerId)
     ;
    }

    public async Task HandleAsync(IDomainEvent<DialogFilterAggregate, DialogFilterId, DialogFilterUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        await NotifyDialogFilterUpdatedAsync(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.Filter.Id,
            domainEvent.AggregateEvent.Filter);
    }

    public async Task HandleAsync(IDomainEvent<DialogFilterAggregate, DialogFilterId, DialogFilterDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        await NotifyDialogFilterUpdatedAsync(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.FilterId,
            null);
    }

    private async Task NotifyDialogFilterUpdatedAsync(RequestInfo requestInfo,
        int filterId,
        DialogFilter? dialogFilter)
    {
        IDialogFilter? filter = null;
        if (dialogFilter != null)
        {
            // Through the converter, not objectMapper: a shareable folder has to go out as
            // dialogFilterChatlist, and the per-layer downgrade happens later in UpdatesResponseService.
            filter = dialogFilterLayeredService.GetConverter(Layers.LayerLatest).ToDialogFilter(dialogFilter);
        }

        var updates = new TUpdateShort
        {
            Update = new TUpdateDialogFilter
            {
                Filter = filter,
                Id = filterId,
            },
            Date = DateTime.UtcNow.ToTimestamp(),
        };

        // The permanent auth key id, not the request's: with PFS the request arrives on a temporary key
        // bound to it, so excluding the temporary id leaves the originating session unexcluded and it is
        // told about the folder it just wrote itself.
        await PushMessageToPeerAsync(new Peer(PeerType.User, requestInfo.UserId), updates,
            requestInfo.PermAuthKeyId);
    }

    /// <summary>
    /// <c>messages.updateDialogFiltersOrder</c>: answer the caller and hand the new order to its other
    /// sessions, so a tab bar reordered on one device is not left stale on the next.
    /// </summary>
    public async Task HandleAsync(
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFiltersOrderUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;

        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, new TBoolTrue());

        var updates = new TUpdateShort
        {
            Update = new TUpdateDialogFilterOrder { Order = new TVector<int>(aggregateEvent.Order) },
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushMessageToPeerAsync(new Peer(PeerType.User, aggregateEvent.OwnerUserId), updates,
            aggregateEvent.RequestInfo.PermAuthKeyId);
    }

    /// <summary>
    /// <c>messages.toggleDialogFilterTags</c>: "If the new value of the toggle is different, the method
    /// will emit an updateDialogFilters to all other currently-logged in sessions, which should trigger a
    /// call to messages.getDialogFilters" — so the push is skipped when nothing changed, while the RPC is
    /// always answered.
    /// </summary>
    public async Task HandleAsync(
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogFilterTagsToggledEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;

        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, new TBoolTrue());

        if (!aggregateEvent.Changed)
        {
            return;
        }

        var updates = new TUpdateShort
        {
            Update = new TUpdateDialogFilters(),
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushMessageToPeerAsync(new Peer(PeerType.User, aggregateEvent.OwnerUserId), updates,
            aggregateEvent.RequestInfo.PermAuthKeyId);
    }

    /// <summary>
    /// <c>messages.toggleDialogPin</c> on an <c>inputDialogPeerFolder</c>. The update carries no
    /// <c>order</c>: with the flag unset Android re-reads <c>messages.getPinnedDialogs</c> for the folder
    /// (<c>loadPinnedDialogs(update.folder_id, 0, null)</c>), which is the only way it can see the
    /// <c>dialogFolder</c> row, and its own reader maps a <c>dialogPeerFolder</c> inside <c>order</c> to
    /// dialog id 0 — so sending a partial order would drop every other pinned chat.
    /// </summary>
    public async Task HandleAsync(
        IDomainEvent<DialogFilterSettingsAggregate, DialogFilterSettingsId, DialogArchivePinnedUpdatedEvent>
            domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;

        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, new TBoolTrue());

        if (!aggregateEvent.Changed)
        {
            return;
        }

        var updates = new TUpdateShort
        {
            Update = new TUpdatePinnedDialogs { FolderId = 0 },
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushMessageToPeerAsync(new Peer(PeerType.User, aggregateEvent.OwnerUserId), updates,
            aggregateEvent.RequestInfo.PermAuthKeyId);
    }

    /// <summary>
    /// <c>folders.editPeerFolders</c>. The answer is an <c>updates</c> carrying a single
    /// <c>updateFolderPeers</c>, and the same update has to reach the user's other sessions: the saga
    /// already consumed a <c>pts</c> for it, so without a stored update every other device is left with a
    /// gap it can never fill through <c>updates.getDifference</c> and keeps showing the chat in the list
    /// it was archived out of.
    /// </summary>
    public async Task HandleAsync(IDomainEvent<EditPeerFoldersSaga, EditPeerFoldersSagaId, EditPeerFoldersCompletedSagaEvent> domainEvent, CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;
        var folderPeers = new TVector<IFolderPeer>(aggregateEvent.FolderPeers.Select(p => new TFolderPeer
        {
            FolderId = p.FolderId,
            Peer = p.Peer.ToPeer()
        }));

        var updateFolderPeers = new TUpdateFolderPeers
        {
            FolderPeers = folderPeers,
            Pts = aggregateEvent.Pts,
            PtsCount = aggregateEvent.PtsCount
        };

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(updateFolderPeers),
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
        };

        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, updates, aggregateEvent.RequestInfo.UserId,
            aggregateEvent.Pts);

        await PushUpdatesToPeerAsync(new Peer(PeerType.User, aggregateEvent.RequestInfo.UserId),
            updates,
            excludeAuthKeyId: aggregateEvent.RequestInfo.PermAuthKeyId,
            pts: aggregateEvent.Pts);
    }
}
