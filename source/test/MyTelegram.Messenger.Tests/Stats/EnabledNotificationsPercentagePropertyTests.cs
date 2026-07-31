using EventFlow.Queries;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;
using FsCheck;
using FsCheck.Xunit;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 3: Enabled-notifications percentage reflects the underlying counts.
///
/// For any subscriber count and notifications-enabled count, <c>enabled_notifications</c> is a
/// <c>statsPercentValue</c> whose <c>part</c> equals the notifications-enabled count and whose <c>total</c>
/// equals the subscriber count.
///
/// Validates: Requirements 2.5.
///
/// The property drives the real <see cref="StatsService.GetBroadcastStatsAsync"/> end to end over an
/// in-memory Metrics_Store seeded with per-day <c>notify_on</c> and <c>muted</c> gauge values (the two
/// families that make up the subscriber count: <c>total = notify_on + muted</c>). The expected
/// notifications-enabled and subscriber counts are computed independently from the seeded fixture data —
/// each gauge contributes its most recent snapshot at or before the Period end (gauges are absolute
/// values, never summed across days) — so the assertion checks the service's wiring
/// (<c>part = notify_on</c>, <c>total = notify_on + muted</c>) rather than re-deriving it from the same
/// aggregation call. Generated days are drawn from a window that always lands inside the reported Period so
/// non-trivial counts flow through, while the empty-data case exercises the <c>{0,0}</c> Period.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a real
/// MongoDB; the <see cref="GraphBuilder"/> (over the shared <see cref="FakeAsyncGraphStore"/>) and Moq
/// stubs for the converter/message/query/mongo collaborators (none of which the broadcast notification path
/// exercises) let the service run without any real infrastructure. Each run executes a minimum of 100
/// generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(EnabledNotificationsArbitraries) }, MaxTest = 100)]
public class EnabledNotificationsPercentagePropertyTests
{
    private const int DefaultReportingWindowDays = 7;

    [Property]
    public void Enabled_notifications_part_and_total_reflect_notify_on_and_subscriber_counts(
        EnabledNotificationsCase testCase)
    {
        var store = new InMemoryMetricsStore();
        var channel = new StatsEntityKey(StatsEntityType.Channel, testCase.ChannelId, 0);

        // Seed the two subscriber-count families through the store's write path.
        foreach (var point in testCase.NotifyOnPoints)
        {
            store.RecordAsync(channel, StatsMetricNames.NotifyOn, point.UtcDay, point.Value).GetAwaiter().GetResult();
        }

        foreach (var point in testCase.MutedPoints)
        {
            store.RecordAsync(channel, StatsMetricNames.Muted, point.UtcDay, point.Value).GetAwaiter().GetResult();
        }

        var service = CreateService(store);

        var result = service
            .GetBroadcastStatsAsync(CreateInput(), testCase.ChannelId, dark: false)
            .GetAwaiter()
            .GetResult();

        // Compute the expected counts independently from the seeded fixture over the Period the store
        // reports (max_date = most recent recorded day, min_date = max_date - window*86400). notify_on and
        // muted are gauges: the range's value is the most recent snapshot at or before the Period end.
        var period = store.GetPeriodAsync(channel, DefaultReportingWindowDays).GetAwaiter().GetResult();
        var expectedNotifyOn = LatestSnapshotAtOrBefore(testCase.NotifyOnPoints, period.MaxDate);
        var expectedMuted = LatestSnapshotAtOrBefore(testCase.MutedPoints, period.MaxDate);
        var expectedSubscribers = expectedNotifyOn + expectedMuted;

        var percent = result.EnabledNotifications.ShouldBeOfType<TStatsPercentValue>();

        // part == notifications-enabled count (Requirement 2.5).
        percent.Part.ShouldBe((double)expectedNotifyOn);

        // total == subscriber count == notify_on + muted (Requirement 2.5).
        percent.Total.ShouldBe((double)expectedSubscribers);
    }

    private static long LatestSnapshotAtOrBefore(IReadOnlyList<DailyMetricPointFixture> points, int maxDay) =>
        points
            .Where(p => p.UtcDay <= maxDay)
            .OrderBy(p => p.UtcDay)
            .Select(p => p.Value)
            .LastOrDefault();

    private static StatsService CreateService(IMetricsStore store)
    {
        var graphBuilder = new GraphBuilder(new FakeAsyncGraphStore());

        // The broadcast notification path uses only the Metrics_Store and the Graph_Builder; the remaining
        // collaborators are never touched, so loose Moq stubs are sufficient.
        return new StatsService(
            store,
            graphBuilder,
            new Mock<IUserConverterService>(MockBehavior.Loose).Object,
            new Mock<IChatConverterService>(MockBehavior.Loose).Object,
            new Mock<IPublicForwardStore>(MockBehavior.Loose).Object,
            new Mock<IAsyncGraphStore>(MockBehavior.Loose).Object,
            new Mock<IMessageConverterService>(MockBehavior.Loose).Object,
            new Mock<IMessageAppService>(MockBehavior.Loose).Object,
            new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            new Mock<IMongoDatabase>(MockBehavior.Loose).Object,
            StatsTestOptions.Create());
    }

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(42);
        return input.Object;
    }

    /// <summary>
    /// An in-memory <see cref="IMetricsStore"/> mirroring the production <see cref="MetricsStore"/>
    /// record/read semantics without MongoDB. Counter metrics accumulate via <c>$inc</c>, gauge metrics
    /// (including <c>notify_on</c> and <c>muted</c>) use set-semantics; <see cref="AggregateAsync"/> sums
    /// counter values across the inclusive range (missing days are 0) and returns the most recent snapshot
    /// at or before the range end for gauges; <see cref="GetPeriodAsync"/>
    /// reports <c>{0,0}</c> when no metric exists and otherwise <c>max_date</c> = most recent recorded day,
    /// <c>min_date = max_date - window*86400</c>. The series/list read paths return empty results (the
    /// broadcast graphs and recent-post list are populated by the service but not asserted by Property 3).
    /// </summary>
    private sealed class InMemoryMetricsStore : IMetricsStore
    {
        private const int SecondsPerDay = 86_400;

        private readonly Dictionary<(StatsEntityKey Entity, string Metric, int UtcDay), long> _values = new();

        public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
            IReadOnlyDictionary<string, long>? breakdown = null)
        {
            var key = (entity, metric, utcDay);
            if (StatsMetricNames.IsGauge(metric))
            {
                _values[key] = delta;
            }
            else
            {
                _values[key] = _values.GetValueOrDefault(key) + delta;
            }

            return Task.CompletedTask;
        }

        public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
        {
            if (maxDayUtc < minDayUtc)
            {
                return Task.FromResult(0L);
            }

            if (StatsMetricNames.IsGauge(metric))
            {
                // Mirrors production: latest snapshot at or before the range end (lower bound ignored).
                var latest = _values
                    .Where(kv => kv.Key.Entity.Equals(entity)
                                 && kv.Key.Metric == metric
                                 && kv.Key.UtcDay <= maxDayUtc)
                    .OrderBy(kv => kv.Key.UtcDay)
                    .Select(kv => kv.Value)
                    .LastOrDefault();
                return Task.FromResult(latest);
            }

            var sum = _values
                .Where(kv => kv.Key.Entity.Equals(entity)
                             && kv.Key.Metric == metric
                             && kv.Key.UtcDay >= minDayUtc
                             && kv.Key.UtcDay <= maxDayUtc)
                .Sum(kv => kv.Value);

            return Task.FromResult(sum);
        }

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays)
        {
            var days = _values
                .Where(kv => kv.Key.Entity.Equals(entity))
                .Select(kv => kv.Key.UtcDay)
                .ToList();

            if (days.Count == 0)
            {
                return Task.FromResult(new StatsDateRange(0, 0));
            }

            var window = Math.Clamp(reportingWindowDays, 1, 365);
            var maxDate = days.Max();
            var minDate = maxDate - window * SecondsPerDay;
            return Task.FromResult(new StatsDateRange(minDate, maxDate));
        }

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
        {
            if (maxDayUtc < minDayUtc)
            {
                return Task.FromResult<IReadOnlyList<DailyPoint>>([]);
            }

            var points = _values
                .Where(kv => kv.Key.Entity.Equals(entity)
                             && kv.Key.Metric == metric
                             && kv.Key.UtcDay >= minDayUtc
                             && kv.Key.UtcDay <= maxDayUtc)
                .OrderBy(kv => kv.Key.UtcDay)
                .Select(kv => new DailyPoint(kv.Key.UtcDay, kv.Value))
                .ToList();

            return Task.FromResult<IReadOnlyList<DailyPoint>>(points);
        }

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            Task.FromResult<IReadOnlyList<CategorySeries>>([]);

        public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            Task.FromResult<IReadOnlyList<PostInteraction>>([]);

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            Task.FromResult(new TopEntities([], [], [], []));
    }
}

/// <summary>
/// A generated Property 3 case: a channel plus sparse per-day <c>notify_on</c> and <c>muted</c> gauge data
/// (unique, aligned days) that together define the subscriber count. Days are drawn from an 8-day window so
/// they always fall within the reported 7-day Period; the empty case yields a <c>{0,0}</c> Period with zero
/// counts.
/// </summary>
public sealed record EnabledNotificationsCase(
    long ChannelId,
    IReadOnlyList<DailyMetricPointFixture> NotifyOnPoints,
    IReadOnlyList<DailyMetricPointFixture> MutedPoints)
{
    public override string ToString() =>
        $"EnabledNotificationsCase(channel={ChannelId}, notifyOn={NotifyOnPoints.Count}, muted={MutedPoints.Count})";
}

/// <summary>FsCheck arbitrary registration for <see cref="EnabledNotificationsCase"/> (Property 3).</summary>
public static class EnabledNotificationsArbitraries
{
    private const int SecondsPerDay = 86_400;
    private const int BaseUtcDay = 1_690_848_000; // 2023-08-01 00:00:00 UTC

    // Day offsets 0..7 (an 8-day span). With the default 7-day reporting window, min_date = max_date - 7
    // days, so every recorded day always lands inside the reported Period.
    private const int WindowDays = 7;

    public static Arbitrary<EnabledNotificationsCase> EnabledNotificationsCase() => Arb.From(Case);

    private static Gen<EnabledNotificationsCase> Case =>
        from channelId in Gen.Choose(1, 20).Select(i => (long)i + 1000)
        from notifyOn in DailyPoints
        from muted in DailyPoints
        select new EnabledNotificationsCase(channelId, notifyOn, muted);

    // A sparse subset of the 8 candidate days (each independently included), with non-negative gauge values.
    private static Gen<IReadOnlyList<DailyMetricPointFixture>> DailyPoints
    {
        get
        {
            var candidates = Enumerable.Range(0, WindowDays + 1)
                .Select(offset => BaseUtcDay + offset * SecondsPerDay)
                .ToArray();

            return StatsGen.ArrayOfLength(
                    candidates.Length,
                    from include in Gen.Frequency(
                        Tuple.Create(2, Gen.Constant(true)),
                        Tuple.Create(1, Gen.Constant(false)))
                    from value in Gen.Choose(0, 1_000_000).Select(i => (long)i)
                    select (include, value))
                .Select(flags => (IReadOnlyList<DailyMetricPointFixture>)candidates
                    .Zip(flags, (day, f) => (day, f))
                    .Where(t => t.f.include)
                    .Select(t => new DailyMetricPointFixture(t.day, t.f.value))
                    .ToList());
        }
    }
}
