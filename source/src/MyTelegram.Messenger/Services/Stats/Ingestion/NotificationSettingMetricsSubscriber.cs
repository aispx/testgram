using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Ingestion subscriber for notification-setting changes on channels (Requirement 10.1).
///
/// <para>Subscribes to <c>PeerNotifySettingsUpdatedEvent</c> and observes mute/unmute transitions targeting
/// a channel. The <c>enabled_notifications</c> statistic (Requirement 2.5) is a channel-wide snapshot whose
/// <c>notify_on</c>/<c>muted</c> values are gauge-family metrics — i.e. absolute counts of subscribers with
/// notifications enabled/disabled.</para>
///
/// <para><b>Ingestion gap:</b> a single per-user notify-setting change does not carry the channel-wide
/// count, and deriving that count requires either the previous per-user state or an aggregation across all
/// of a channel's subscribers' notify settings. A gauge cannot be maintained from an isolated per-user
/// delta (recording <c>+1</c>/<c>-1</c> against a set-semantics gauge is not meaningful). This subscriber is
/// therefore wired to the transition but defers the absolute-count computation to a dedicated notify-state
/// aggregation, which is a documented gap. It records nothing to avoid corrupting the gauge, and logs the
/// observed transition for traceability.</para>
/// </summary>
public sealed class NotificationSettingMetricsSubscriber(
    ILogger<NotificationSettingMetricsSubscriber> logger)
    : ISubscribeSynchronousTo<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent>
{
    public Task HandleAsync(
        IDomainEvent<PeerNotifySettingsAggregate, PeerNotifySettingsId, PeerNotifySettingsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        if (e.PeerType != PeerType.Channel)
        {
            return Task.CompletedTask;
        }

        var settings = e.PeerNotifySettings;
        var muted = (settings.Silent ?? false) || (settings.MuteUntil ?? 0) > DateTime.UtcNow.ToTimestamp();

        // See class remarks: channel-wide notify_on/muted gauges require a notify-state aggregation that a
        // single per-user event cannot supply. Wired-but-deferred (documented gap).
        logger.LogDebug(
            "Stats ingestion observed notify-setting change: channelId={ChannelId} userId={UserId} muted={Muted}",
            e.PeerId, e.OwnerPeerId, muted);

        return Task.CompletedTask;
    }
}
