using EventFlow.Subscribers;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Ingestion subscriber for subscriber-count changes (Requirement 10.1).
///
/// <para>Subscribes to channel-membership domain events and records the affected channel's current
/// absolute subscriber/member count as a gauge for the UTC day the change occurred. <c>followers</c> is
/// recorded for broadcast channels and <c>members</c> for supergroups (both are gauge-family metrics, so
/// repeated recording on the same day is idempotent — the latest absolute count wins).</para>
///
/// <para>Joins additionally record two counter breakdowns: <c>joins_by_source</c> keyed by the join method
/// (<see cref="ChatJoinType"/> — invite link, join request, admin invitation, self-join) feeding the
/// new-followers/members-by-source graphs, and <c>joins_by_language</c> keyed by the joining user's device
/// language code (from the device read model) feeding the languages graph. Joins and leaves also bump the
/// supergroup <c>actions</c> counter.</para>
///
/// <para>The absolute count is read from the channel read model (<see cref="IChannelAppService"/>). Because
/// the participant-count read model is updated by a separate command, the value observed here may lag a
/// single membership change by an eventual-consistency window; subsequent membership events converge the
/// recorded daily gauge to the correct value.</para>
/// </summary>
public sealed class SubscriberCountMetricsSubscriber(
    IMetricsStore metricsStore,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase,
    NotifyStateRecorder notifyStateRecorder,
    ILogger<SubscriberCountMetricsSubscriber> logger)
    : ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberCreatedEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelCreatorCreatedEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent>,
        ISubscribeSynchronousTo<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent2>
{
    private const string DeviceReadModelCollection = "eventflow-devicereadmodel";

    public async Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        await RecordSubscriberCountAsync(e.ChannelId, e.Date, isJoinOrLeave: true);
        await RecordJoinBreakdownsAsync(e.ChannelId, e.UserId, e.ChatJoinType, e.Date);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelCreatorCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, e.Date, isJoinOrLeave: false);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, eventDate: null, isJoinOrLeave: true);
    }

    public Task HandleAsync(
        IDomainEvent<ChannelMemberAggregate, ChannelMemberId, ChannelMemberLeftEvent2> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        return RecordSubscriberCountAsync(e.ChannelId, eventDate: null, isJoinOrLeave: true);
    }

    private async Task RecordSubscriberCountAsync(long channelId, int? eventDate, bool isJoinOrLeave)
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

        // Membership changes are the second series of the supergroup activity ("actions") graph; message
        // posts are the first and are recorded by the message subscriber.
        if (isJoinOrLeave && channel.MegaGroup)
        {
            await metricsStore.RecordAsync(entity, StatsMetricNames.Actions, utcDay, 1);
        }

        // notify_on is derived from the participant count, so it must be refreshed here too — otherwise
        // the notifications percentage stays pinned to the membership at the last mute/unmute.
        try
        {
            await notifyStateRecorder.RecordAsync(channelId, count, actorUserId: null, actorMuted: false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Stats ingestion failed to refresh notify gauges after a membership change: channelId={ChannelId}",
                channelId);
        }
    }

    private async Task RecordJoinBreakdownsAsync(long channelId, long userId, ChatJoinType joinType, int eventDate)
    {
        var utcDay = StatsIngestionTime.ToUtcDayOrNow(eventDate);
        var entity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);

        await metricsStore.RecordAsync(entity, StatsMetricNames.JoinsBySource, utcDay, 1,
            new Dictionary<string, long> { [JoinSourceKey(joinType)] = 1 });

        var language = await ResolveUserLanguageAsync(userId);
        await metricsStore.RecordAsync(entity, StatsMetricNames.JoinsByLanguage, utcDay, 1,
            new Dictionary<string, long> { [language] = 1 });
    }

    private static string JoinSourceKey(ChatJoinType joinType) => joinType switch
    {
        ChatJoinType.ByLink => "Invite links",
        ChatJoinType.ByRequest => "Join requests",
        ChatJoinType.InvitedByAdmin => "Invitations",
        ChatJoinType.BySelf => "Search",
        _ => "Other"
    };

    /// <summary>
    /// Resolves the joining user's language from their most recent active device registration
    /// (<c>initConnection</c> lang_code); falls back to "other" when the user has no device on record.
    /// </summary>
    private async Task<string> ResolveUserLanguageAsync(long userId)
    {
        var doc = await mongoDatabase.GetCollection<BsonDocument>(DeviceReadModelCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId) &
                  Builders<BsonDocument>.Filter.Eq("IsActive", true))
            .Limit(1)
            .FirstOrDefaultAsync();

        if (doc == null)
        {
            return "other";
        }

        var lang = GetString(doc, "LangCode") ?? GetString(doc, "SystemLangCode");
        if (string.IsNullOrWhiteSpace(lang))
        {
            return "other";
        }

        // Normalize "en-US"-style codes to the base language.
        var dash = lang.IndexOf('-');
        return (dash > 0 ? lang[..dash] : lang).ToLowerInvariant();
    }

    private static string? GetString(BsonDocument doc, string field) =>
        doc.Contains(field) && doc[field].IsString ? doc[field].AsString : null;
}
