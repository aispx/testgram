using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Domain.Aggregates.PeerNotifySetting;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Applies <c>globalPrivacySettings.archive_and_mute_new_noncontact_peers</c>: the first
/// message from a non-contact lands in the archive folder, muted.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
/// <remarks>
/// <para>
/// The setting was stored and echoed back to clients but never acted on, so enabling
/// "archive and mute new chats from unknown users" did nothing on the server.
/// </para>
/// <para>
/// Only reacts when the inbox message created the dialog. Archiving on every message would
/// drag a chat back into the archive each time the stranger writes again, even after the
/// user deliberately moved it out.
/// </para>
/// </remarks>
public class AutoArchiveNonContactDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IPrivacyAppService privacyAppService,
    IContactAppService contactAppService,
    IUserAppService userAppService)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<DialogAggregate, DialogId, InboxMessageReceivedEvent>
{
    /// <summary>Archive folder id, as used by <c>folders.editPeerFolders</c>.</summary>
    private const int ArchiveFolderId = 1;

    public async Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, InboxMessageReceivedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;
        if (!aggregateEvent.IsNewDialog)
        {
            return;
        }

        var ownerUserId = aggregateEvent.OwnerPeerId;
        var toPeer = aggregateEvent.ToPeer;

        // Group and channel chats are out of scope: the setting only covers private chats
        // with users who are not in the recipient's contact list.
        if (toPeer.PeerType != PeerType.User || toPeer.PeerId == ownerUserId)
        {
            return;
        }

        var globalPrivacySettings = await privacyAppService.GetGlobalPrivacySettingsAsync(ownerUserId);
        if (!(globalPrivacySettings?.ArchiveAndMuteNewNoncontactPeers ?? false))
        {
            return;
        }

        var contactType = await contactAppService.GetContactTypeAsync(ownerUserId, toPeer.PeerId);
        if (contactType is ContactType.Mutual or ContactType.ContactOfTargetUser)
        {
            return;
        }

        // Service notifications are not a "non-contact peer" in the sense the setting means.
        if (toPeer.PeerId == MyTelegramConsts.NotificationServiceUserId)
        {
            return;
        }

        var senderReadModel = await userAppService.GetAsync(toPeer.PeerId);
        if (senderReadModel == null)
        {
            return;
        }

        await commandBus.PublishAsync(new UpdateDialogFolderCommand(
            DialogId.Create(ownerUserId, toPeer),
            aggregateEvent.RequestInfo,
            ArchiveFolderId), cancellationToken);

        await commandBus.PublishAsync(new UpdatePeerNotifySettingsCommand(
            PeerNotifySettingsId.Create(ownerUserId, toPeer.PeerType, toPeer.PeerId),
            aggregateEvent.RequestInfo,
            ownerUserId,
            toPeer.PeerType,
            toPeer.PeerId,
            showPreviews: null,
            silent: true,
            muteUntil: int.MaxValue,
            sound: string.Empty), cancellationToken);
    }
}
