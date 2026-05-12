namespace MyTelegram.Domain.Aggregates.Channel;

public partial class MonoforumEnabledEvent(
    RequestInfo requestInfo,
    long channelId,
    bool isMonoforum,
    bool broadcastMessagesAllowed,
    long? linkedMonoforumId,
    long? sendPaidMessagesStars = null
) : RequestAggregateEvent2<ChannelAggregate, ChannelId>(requestInfo)
{
    public long ChannelId { get; } = channelId;
    public bool IsMonoforum { get; } = isMonoforum;
    public bool BroadcastMessagesAllowed { get; } = broadcastMessagesAllowed;
    public long? LinkedMonoforumId { get; } = linkedMonoforumId;
    public long? SendPaidMessagesStars { get; } = sendPaidMessagesStars;
}
