using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Ingestion subscriber for notification-setting changes on channels (Requirement 10.1).
///
/// <para>Subscribes to <c>PeerNotifySettingsUpdatedEvent</c> and refreshes the channel-wide
/// <c>muted</c>/<c>notify_on</c> gauges that feed <c>enabled_notifications</c> (Requirement 2.5) and the
/// mute graph. The recomputation itself lives in <see cref="NotifyStateRecorder"/>, which is shared with
/// the membership subscriber so both gauges stay current as the subscriber count changes.</para>
/// </summary>
public sealed class NotificationSettingMetricsSubscriber(
    NotifyStateRecorder notifyStateRecorder,
    IChannelAppService channelAppService,
    ILogger<NotificationSettingMetricsSubscriber> logger)
    : ISubscribeSynchronousTo<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        if (e.PeerType != PeerType.Channel)
        {
            return;
        }

        var channel = await channelAppService.GetAsync((long?)e.PeerId);
        if (channel == null)
        {
            return;
        }

        var settings = e.PeerNotifySettings;
        var actorMuted = (settings.Silent ?? false)
                         || (settings.MuteUntil ?? 0) > DateTime.UtcNow.ToTimestamp();

        try
        {
            await notifyStateRecorder.RecordAsync(e.PeerId, channel.ParticipantsCount ?? 0, e.OwnerPeerId,
                actorMuted, cancellationToken);
        }
        catch (Exception ex)
        {
            // Stats ingestion must never fail the notify-settings command itself.
            logger.LogWarning(ex,
                "Stats ingestion failed to record notify gauges: channelId={ChannelId} userId={UserId}",
                e.PeerId, e.OwnerPeerId);
        }
    }
}
