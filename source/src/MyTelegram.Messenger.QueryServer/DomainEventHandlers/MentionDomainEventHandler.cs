using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Tells the other sessions of a user that a mention lost its @ badge, so they do not have to wait for
/// the next getDifference. See https://corefork.telegram.org/api/mentions
/// </summary>
public class MentionDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IPtsHelper ptsHelper)
    : DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<DialogAggregate, DialogId, MentionReadEvent>
{
    public async Task HandleAsync(IDomainEvent<DialogAggregate, DialogId, MentionReadEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var aggregateEvent = domainEvent.AggregateEvent;
        var ownerUserId = aggregateEvent.OwnerUserId;
        var toPeer = aggregateEvent.ToPeer;

        IUpdate update = toPeer.PeerType == PeerType.Channel
            ? new TUpdateChannelReadMessagesContents
            {
                ChannelId = toPeer.PeerId,
                Messages = new TVector<int>(aggregateEvent.MessageId)
            }
            : new TUpdateReadMessagesContents
            {
                Messages = new TVector<int>(aggregateEvent.MessageId),
                // The mention counter is not part of the pts sequence: report the current value with
                // an empty count so the client applies the update without a difference gap.
                Pts = ptsHelper.GetCachedPts(ownerUserId),
                PtsCount = 0,
                Date = DateTime.UtcNow.ToTimestamp()
            };

        var updates = new TUpdateShort
        {
            Update = update,
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushUpdatesToPeerAsync(ownerUserId.ToUserPeer(), updates);
    }
}
