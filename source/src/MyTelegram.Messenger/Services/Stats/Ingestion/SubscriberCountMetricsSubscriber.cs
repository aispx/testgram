using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Ingestion subscriber for subscriber-count changes (Requirement 10.1).
///
/// <para>Subscribes to channel-membership domain events and records the affected channel's current
/// absolute subscriber/member count as a gauge for the UTC day the change occurred. <c>followers</c> is
/// recorded for broadcast channels and <c>members</c> for supergroups (both are gauge-family metrics, so
/// repeated recording on the same day is idempotent — the latest absolute count wins).</para>
///
/// <para>The absolute count is read from the channel read model (<see cref="IChannelAppService"/>). Because
/// the participant-count read model is updated by a separate command, the value observed here may lag a
/// single membership change by an eventual-consistency window; subsequent membership events converge the
/// recorded daily gauge to the correct value.</para>
/// </summary>
public sealed class SubscriberCountMetricsSubscriber(
    IMetricsStore metricsStore,
    IChannelAppService channelAppService)
    : ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberCreatedEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelCreatorCreatedEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent2>
{
    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, e.Date);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelCreatorCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, e.Date);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, eventDate: null);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent2> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, eventDate: null);
    }

    private async Task RecordSubscriberCountAsync(long channelId, int? eventDate)
    {
        var channel = await channelAppService.GetAsync((long?)channelId);
        if (channel == null)
        {
            return;
        }

        var count = channel.ParticipantsCount ?? 0;
        var utcDay = StatsIngestionTime.ToUtcDayOrNow(eventDate ?? 0);
        var entity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var metric = channel.MegaGroup ? StatsMetricNames.Members : StatsMetricNames.Followers;

        await metricsStore.RecordAsync(entity, metric, utcDay, count);
    }
}
