using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Recomputes and records a channel's <c>muted</c>/<c>notify_on</c> gauges, which back
/// <c>enabled_notifications</c> (Requirement 2.5) and the mute graph.
///
/// <para>Both gauges must be refreshed whenever either operand changes — a notify-setting change
/// (<see cref="NotificationSettingMetricsSubscriber"/>) or a membership change
/// (<see cref="SubscriberCountMetricsSubscriber"/>) — otherwise the pair goes stale against a growing
/// subscriber count.</para>
///
/// <para>The muted count is derived from the per-user notify-settings read model, which retains documents
/// for users who left the channel (or never joined, having only previewed it), so it is clamped to the
/// current participant count: <c>notify_on = participants - min(muted, participants)</c>.</para>
/// </summary>
public sealed class NotifyStateRecorder(IMetricsStore metricsStore, IMongoDatabase mongoDatabase)
    : ISingletonDependency
{
    private const string NotifySettingsCollection = "eventflow-peernotifysettingsreadmodel";

    /// <summary>
    /// Recomputes both gauges for <paramref name="channelId"/> and records them for the current UTC day.
    /// <paramref name="actorUserId"/>/<paramref name="actorMuted"/> fold in a notify-setting change whose
    /// read-model projection may not have landed yet; pass <see langword="null"/> when no user acted.
    /// </summary>
    public async Task RecordAsync(long channelId, long participantsCount, long? actorUserId, bool actorMuted,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToTimestamp();

        var filter = Builders<BsonDocument>.Filter.Eq("PeerId", channelId)
                     & Builders<BsonDocument>.Filter.Eq("PeerType", (int)PeerType.Channel)
                     & (Builders<BsonDocument>.Filter.Eq("NotifySettings.Silent", true)
                        | Builders<BsonDocument>.Filter.Gt("NotifySettings.MuteUntil", now));

        if (actorUserId.HasValue)
        {
            filter &= Builders<BsonDocument>.Filter.Ne("OwnerPeerId", actorUserId.Value);
        }

        var muted = await mongoDatabase.GetCollection<BsonDocument>(NotifySettingsCollection)
            .CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        if (actorUserId.HasValue && actorMuted)
        {
            muted += 1;
        }

        // Notify settings survive leaving the channel, so the raw count can exceed the membership.
        var participants = Math.Max(0, participantsCount);
        muted = Math.Min(muted, participants);

        var utcDay = StatsIngestionTime.CurrentUtcDay();
        var entity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        await metricsStore.RecordAsync(entity, StatsMetricNames.Muted, utcDay, muted);
        await metricsStore.RecordAsync(entity, StatsMetricNames.NotifyOn, utcDay, participants - muted);
    }
}
