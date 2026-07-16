using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Ingestion subscriber for channel message activity (Requirement 10.1): posts, shares/forwards and
/// reactions. Upserts per-day counters into the Metrics_Store, bucketed by the UTC calendar day the event
/// occurred, keyed both by the owning channel and by the individual message so that both aggregate channel
/// statistics and per-message statistics can be derived.
///
/// <para>Only channel-owned messages are recorded — private-chat and small-group traffic does not
/// contribute to channel/supergroup statistics.</para>
///
/// <para><b>Views:</b> per-view increments flow through
/// <c>MessageAggregate.MessageViewsIncrementedEvent</c>, which carries only the numeric message id and the
/// new view total — it does not carry the owning channel/peer, so a view increment cannot be attributed to
/// a metrics entity. This subscriber therefore records the post's initial view count at creation time; the
/// running per-view increments are a documented ingestion gap pending enrichment of the view event with
/// its owner peer.</para>
/// </summary>
public sealed class MessageMetricsSubscriber(IMetricsStore metricsStore)
    : ISubscribeSynchronousTo<MessageAggregate, MessageId, OutboxMessageCreatedEvent>,
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageForwardedEvent>,
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageReactionsUpdatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.OutboxMessageItem;
        if (item.ToPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        var channelId = item.ToPeer.PeerId;
        var utcDay = StatsIngestionTime.ToUtcDayOrNow(item.Date);
        var channelEntity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var messageEntity = new StatsEntityKey(StatsEntityType.Message, channelId, item.MessageId);

        // Channel post/message count (used for supergroup "messages" and post enumeration).
        await metricsStore.RecordAsync(channelEntity, StatsMetricNames.Messages, utcDay, 1);

        // Per-message post date gauge (enables newest-first recent-post ordering).
        await metricsStore.RecordAsync(messageEntity, StatsMetricNames.PostDate, utcDay,
            item.Date > 0 ? item.Date : StatsIngestionTime.CurrentUtcDay());

        // Initial view count captured at post time (see class remarks on the per-view gap).
        if (item.Views is > 0)
        {
            await metricsStore.RecordAsync(messageEntity, StatsMetricNames.Views, utcDay, item.Views.Value);
        }

        // Top-poster breakdown keyed by the posting user id (supergroup top posters).
        var posterUserId = item.SenderUserId != 0 ? item.SenderUserId : item.SenderPeer.PeerId;
        if (posterUserId != 0)
        {
            var posterKey = posterUserId.ToString();
            await metricsStore.RecordAsync(channelEntity, StatsMetricNames.TopPosterMessages, utcDay, 1,
                new Dictionary<string, long> { [posterKey] = 1 });

            var chars = item.Message?.Length ?? 0;
            if (chars > 0)
            {
                await metricsStore.RecordAsync(channelEntity, StatsMetricNames.TopPosterChars, utcDay, chars,
                    new Dictionary<string, long> { [posterKey] = chars });
            }
        }
    }

    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageForwardedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        // The forwarded (source) message; shares are attributed to the source channel post.
        var source = domainEvent.AggregateEvent.OriginalMessageItem;
        if (source.OwnerPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        var channelId = source.OwnerPeer.PeerId;
        var utcDay = StatsIngestionTime.CurrentUtcDay();
        var channelEntity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var messageEntity = new StatsEntityKey(StatsEntityType.Message, channelId, source.MessageId);

        await metricsStore.RecordAsync(channelEntity, StatsMetricNames.Shares, utcDay, 1);
        await metricsStore.RecordAsync(messageEntity, StatsMetricNames.Shares, utcDay, 1);
    }

    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, MessageReactionsUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        var item = e.MessageItem;
        if (item.ToPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        // The reactions list is the full post-update snapshot; the delta attributable to this event is the
        // acting user's reactions. Reactions are recorded as a per-day activity counter (removals are not
        // decremented).
        var actorReactions = e.Reactions.Where(r => r.UserId == e.RequestInfo.UserId).ToList();
        if (actorReactions.Count == 0)
        {
            return;
        }

        var channelId = item.ToPeer.PeerId;
        var utcDay = StatsIngestionTime.CurrentUtcDay();
        var channelEntity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var messageEntity = new StatsEntityKey(StatsEntityType.Message, channelId, item.MessageId);

        var emotionBreakdown = new Dictionary<string, long>();
        foreach (var reaction in actorReactions)
        {
            var emotion = GetEmotionKey(reaction);
            emotionBreakdown[emotion] = emotionBreakdown.GetValueOrDefault(emotion) + 1;
        }

        await metricsStore.RecordAsync(channelEntity, StatsMetricNames.Reactions, utcDay, actorReactions.Count);
        await metricsStore.RecordAsync(messageEntity, StatsMetricNames.Reactions, utcDay, actorReactions.Count,
            emotionBreakdown);
    }

    // Groups a reaction into an emotion category key for reactions-by-emotion breakdowns.
    private static string GetEmotionKey(Reaction reaction)
    {
        if (reaction.IsPaid)
        {
            return "paid";
        }

        if (reaction.CustomEmojiDocumentId.HasValue)
        {
            return $"custom:{reaction.CustomEmojiDocumentId.Value}";
        }

        return string.IsNullOrEmpty(reaction.Emoticon) ? "unknown" : reaction.Emoticon;
    }
}
