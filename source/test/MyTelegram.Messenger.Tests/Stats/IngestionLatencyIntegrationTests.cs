using System.Diagnostics;
using EventFlow.Aggregates;
using MyTelegram;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stats.Ingestion;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 10.3 — integration test for ingestion latency and wiring (Requirement 10.1).
///
/// <para>Requirement 10.1: when a statistics-relevant event occurs for a Channel, the Metrics_Store SHALL,
/// within 60 seconds of the event, record it against the affected entity and against the UTC calendar day
/// the event occurred in.</para>
///
/// <para>This is an <b>integration</b> test (not a property test): it exercises the real ingestion
/// subscriber → real production <see cref="MetricsStore"/> → real MongoDB round-trip end to end. Unlike the
/// storage property/unit tests (which run against in-memory stores), this test wires the MongoDB-backed
/// <see cref="MetricsStore"/> to an actual <c>mongod</c> instance via <see cref="EmbeddedMongoServer"/>, so
/// it validates the true persistence path — index creation, the <c>$inc</c> upsert, and read-back.</para>
///
/// <para>It records a channel post through <see cref="MessageMetricsSubscriber"/> (the same code path the
/// event bus drives in production) and asserts that the resulting per-day counters are retrievable from the
/// store, bucketed to the event's UTC calendar day, and that the record→read latency is comfortably inside
/// the 60-second window. When no MongoDB is available the whole test is skipped cleanly via
/// <see cref="RequiresMongoDbFactAttribute"/>.</para>
/// </summary>
public class IngestionLatencyIntegrationTests
{
    private const int SecondsPerDay = 86_400;

    // A fixed, day-aligned reference timestamp so the assertion on the bucket is deterministic:
    // 2023-08-01 12:34:56 UTC (mid-day, so it must bucket down to that day's 00:00:00 UTC).
    private const int PostDateUtc = 1_690_848_000 + 12 * 3600 + 34 * 60 + 56;
    private const int ExpectedUtcDay = 1_690_848_000; // 2023-08-01 00:00:00 UTC

    [RequiresMongoDbFact]
    public async Task Channel_post_event_is_reflected_in_MetricsStore_within_the_60_second_window()
    {
        using var mongo = EmbeddedMongoServer.Start();

        // Real production store over a real MongoDB database — no mocks, no in-memory substitute.
        IMetricsStore metricsStore = new MetricsStore(mongo.Database);
        var subscriber = new MessageMetricsSubscriber(metricsStore);

        const long channelId = 777_001;
        const int msgId = 42;
        const int initialViews = 5;
        const long posterUserId = 900_500;

        var domainEvent = BuildChannelPostEvent(channelId, msgId, PostDateUtc, initialViews, posterUserId);

        // The event occurs "now" from the store's perspective; measure the record→read latency.
        var stopwatch = Stopwatch.StartNew();
        await subscriber.HandleAsync(domainEvent, CancellationToken.None);

        var channelEntity = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var messageEntity = new StatsEntityKey(StatsEntityType.Message, channelId, msgId);

        // Requirement 10.1: recorded against the affected entity and the UTC calendar day of the event.
        // Query the single event day (a one-day inclusive range) to also assert the day-bucketing.
        var channelMessages = await metricsStore.AggregateAsync(
            channelEntity, StatsMetricNames.Messages, ExpectedUtcDay, ExpectedUtcDay);
        var messageViews = await metricsStore.AggregateAsync(
            messageEntity, StatsMetricNames.Views, ExpectedUtcDay, ExpectedUtcDay);
        stopwatch.Stop();

        // The channel post counter and the per-message initial views were persisted and read back.
        channelMessages.ShouldBe(1);
        messageViews.ShouldBe(initialViews);

        // The per-message series confirms the value landed in exactly the event's UTC day bucket.
        var viewsSeries = await metricsStore.GetSeriesAsync(
            messageEntity, StatsMetricNames.Views, ExpectedUtcDay - SecondsPerDay, ExpectedUtcDay + SecondsPerDay);
        viewsSeries.Count.ShouldBe(1);
        viewsSeries[0].UtcDay.ShouldBe(ExpectedUtcDay);
        viewsSeries[0].Value.ShouldBe(initialViews);

        // Ingestion latency budget (Requirement 10.1): the record→read round-trip is well under 60 seconds.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }

    [RequiresMongoDbFact]
    public async Task Non_channel_message_event_records_nothing_in_the_store()
    {
        using var mongo = EmbeddedMongoServer.Start();

        IMetricsStore metricsStore = new MetricsStore(mongo.Database);
        var subscriber = new MessageMetricsSubscriber(metricsStore);

        const long userPeerId = 12_345; // a private (user) conversation, not a channel
        const int msgId = 7;

        var toPeer = new Peer(PeerType.User, userPeerId);
        var senderPeer = new Peer(PeerType.User, userPeerId);
        var item = new MessageItem(
            OwnerPeer: senderPeer,
            ToPeer: toPeer,
            SenderPeer: senderPeer,
            SenderUserId: userPeerId,
            MessageId: msgId,
            Message: "hi",
            Date: PostDateUtc,
            RandomId: 1,
            IsOut: true,
            Views: 3);
        var domainEvent = WrapOutboxCreated(userPeerId, msgId, item);

        await subscriber.HandleAsync(domainEvent, CancellationToken.None);

        // Non-channel traffic must not contribute to channel/supergroup statistics.
        var channelEntity = new StatsEntityKey(StatsEntityType.Channel, userPeerId, 0);
        var period = await metricsStore.GetPeriodAsync(channelEntity, reportingWindowDays: 7);
        period.MinDate.ShouldBe(0);
        period.MaxDate.ShouldBe(0);
    }

    private static IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> BuildChannelPostEvent(
        long channelId, int msgId, int date, int views, long posterUserId)
    {
        var channelPeer = new Peer(PeerType.Channel, channelId);
        var senderPeer = new Peer(PeerType.User, posterUserId);
        var item = new MessageItem(
            OwnerPeer: channelPeer,
            ToPeer: channelPeer,
            SenderPeer: senderPeer,
            SenderUserId: posterUserId,
            MessageId: msgId,
            Message: "hello channel",
            Date: date,
            RandomId: 1,
            IsOut: true,
            Post: true,
            Views: views);

        return WrapOutboxCreated(channelId, msgId, item);
    }

    private static IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> WrapOutboxCreated(
        long ownerPeerId, int msgId, MessageItem item)
    {
        var aggregateEvent = new OutboxMessageCreatedEvent(
            RequestInfo.Empty,
            item,
            mentionedUserIds: null,
            replyToMsgItems: null,
            clearDraft: true,
            groupItemCount: 1,
            linkedChannelId: null,
            chatMembers: null);

        return new DomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent>(
            aggregateEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            MessageId.Create(ownerPeerId, msgId),
            1);
    }
}
