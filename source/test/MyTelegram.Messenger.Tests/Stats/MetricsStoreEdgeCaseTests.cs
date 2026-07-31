using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Messenger.Services.Stats.Ingestion;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 3.7 — example/edge-case unit tests for the Metrics_Store.
///
/// These pin down three boundary behaviours that the Metrics_Store property tests (Properties 15/16) do
/// not force on every generated run:
/// <list type="bullet">
///   <item><c>period {0,0}</c> when the entity has no recorded metric on any day (Requirement 10.4).</item>
///   <item>A day within a range that has no recorded metric contributes <c>0</c> to the aggregate — the
///   zero-fill semantics of <c>AggregateAsync</c> (Requirement 10.5).</item>
///   <item>UTC day-bucketing of a timestamp aligns to <c>00:00:00 UTC</c> of the day it falls in — the
///   pure part of ingestion recording (Requirement 10.1), exercised directly against
///   <see cref="StatsIngestionTime.ToUtcDay"/>.</item>
/// </list>
///
/// Per the tasks.md notes, storage tests run against an in-memory store rather than a real MongoDB. The
/// nested <see cref="InMemoryMetricsStore"/> faithfully mirrors the documented production
/// <see cref="MetricsStore"/> semantics for the members exercised here — <c>$inc</c> accumulation for
/// counters, set-semantics for the absolute-gauge family, <c>{0,0}</c> when no metric exists, and
/// sum-with-zero-fill for <c>AggregateAsync</c>. The UTC day-bucketing case needs no store: it tests the
/// pure helper directly.
/// </summary>
public class MetricsStoreEdgeCaseTests
{
    private const int SecondsPerDay = 86_400;

    // Aligned reference day: 2023-08-01 00:00:00 UTC (a multiple of 86400).
    private const int Day2023Aug01 = 1_690_848_000;

    // ----- period {0,0} when no metric on any day (Requirement 10.4) -----

    [Fact]
    public void GetPeriod_returns_zero_zero_when_entity_has_no_recorded_metric()
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        var period = store.GetPeriodAsync(entity, reportingWindowDays: 7).GetAwaiter().GetResult();

        period.MinDate.ShouldBe(0);
        period.MaxDate.ShouldBe(0);
    }

    [Fact]
    public void GetPeriod_returns_zero_zero_when_only_a_different_entity_has_metrics()
    {
        var store = new InMemoryMetricsStore();
        var target = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);
        var other = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 999, ItemId: 0);

        // Recording metrics for a different entity must not give the target a non-empty period.
        store.RecordAsync(other, StatsMetricNames.Views, Day2023Aug01, 5).GetAwaiter().GetResult();

        var period = store.GetPeriodAsync(target, reportingWindowDays: 7).GetAwaiter().GetResult();

        period.MinDate.ShouldBe(0);
        period.MaxDate.ShouldBe(0);
    }

    [Fact]
    public void GetPeriod_returns_a_non_empty_range_once_a_metric_exists()
    {
        // Complements the {0,0} case: once at least one metric is recorded the period is no longer {0,0},
        // so the empty-entity branch is a genuinely distinct behaviour.
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        store.RecordAsync(entity, StatsMetricNames.Views, Day2023Aug01, 5).GetAwaiter().GetResult();

        var period = store.GetPeriodAsync(entity, reportingWindowDays: 7).GetAwaiter().GetResult();

        period.MaxDate.ShouldBe(Day2023Aug01);
        period.MinDate.ShouldBe(Day2023Aug01 - 7 * SecondsPerDay);
    }

    // ----- missing metric day treated as 0 in AggregateAsync (Requirement 10.5) -----

    [Fact]
    public void Aggregate_treats_a_gap_day_within_the_range_as_zero()
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        var day0 = Day2023Aug01;
        // Day2023Aug01 + SecondsPerDay is the gap day: intentionally left with no recorded metric.
        var day2 = Day2023Aug01 + 2 * SecondsPerDay;

        store.RecordAsync(entity, StatsMetricNames.Views, day0, 3).GetAwaiter().GetResult();
        store.RecordAsync(entity, StatsMetricNames.Views, day2, 4).GetAwaiter().GetResult();

        // Range spans all three days; the missing day1 contributes 0, so the sum is 3 + 0 + 4 = 7.
        var aggregate = store.AggregateAsync(entity, StatsMetricNames.Views, day0, day2)
            .GetAwaiter().GetResult();

        aggregate.ShouldBe(7);
    }

    [Fact]
    public void Aggregate_of_a_gauge_returns_the_latest_snapshot_not_the_sum()
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        var day0 = Day2023Aug01;
        var day2 = Day2023Aug01 + 2 * SecondsPerDay;

        // Two absolute follower-count snapshots; summing them (23 + 25) would be wrong — the range's
        // value is the most recent snapshot.
        store.RecordAsync(entity, StatsMetricNames.Followers, day0, 23).GetAwaiter().GetResult();
        store.RecordAsync(entity, StatsMetricNames.Followers, day2, 25).GetAwaiter().GetResult();

        var aggregate = store.AggregateAsync(entity, StatsMetricNames.Followers, day0, day2)
            .GetAwaiter().GetResult();

        aggregate.ShouldBe(25);

        // A range starting after the last snapshot still sees it: an absolute value persists.
        var later = store.AggregateAsync(
                entity, StatsMetricNames.Followers, day2 + SecondsPerDay, day2 + 3 * SecondsPerDay)
            .GetAwaiter().GetResult();

        later.ShouldBe(25);
    }

    [Fact]
    public void Aggregate_returns_zero_when_no_metric_exists_for_any_day_in_the_range()
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        // No metric recorded at all: every day in the range is a missing day and contributes 0.
        var aggregate = store.AggregateAsync(
                entity, StatsMetricNames.Views, Day2023Aug01, Day2023Aug01 + 6 * SecondsPerDay)
            .GetAwaiter().GetResult();

        aggregate.ShouldBe(0);
    }

    [Fact]
    public void Aggregate_excludes_days_outside_the_range_and_zero_fills_the_rest()
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 100, ItemId: 0);

        var dayBefore = Day2023Aug01 - SecondsPerDay;      // outside (below) the queried range
        var dayIn = Day2023Aug01;                          // inside the queried range
        var dayAfter = Day2023Aug01 + 5 * SecondsPerDay;   // outside (above) the queried range

        store.RecordAsync(entity, StatsMetricNames.Views, dayBefore, 100).GetAwaiter().GetResult();
        store.RecordAsync(entity, StatsMetricNames.Views, dayIn, 9).GetAwaiter().GetResult();
        store.RecordAsync(entity, StatsMetricNames.Views, dayAfter, 100).GetAwaiter().GetResult();

        // Range covers only [dayIn, dayIn + 1 day]; the out-of-range days are excluded and the empty
        // second day zero-fills, so only the single in-range value remains.
        var aggregate = store.AggregateAsync(entity, StatsMetricNames.Views, dayIn, dayIn + SecondsPerDay)
            .GetAwaiter().GetResult();

        aggregate.ShouldBe(9);
    }

    // ----- UTC day-bucketing of a timestamp (Requirement 10.1 pure part) -----

    [Fact]
    public void ToUtcDay_of_a_timestamp_already_on_a_day_boundary_is_unchanged()
    {
        StatsIngestionTime.ToUtcDay(Day2023Aug01).ShouldBe(Day2023Aug01);
    }

    [Theory]
    [InlineData(1)]                    // one second past midnight
    [InlineData(60)]                   // one minute past midnight
    [InlineData(3_600)]                // one hour past midnight
    [InlineData(43_200)]               // noon
    [InlineData(SecondsPerDay - 1)]    // last second of the day
    public void ToUtcDay_buckets_any_time_within_a_day_down_to_that_days_midnight(int offsetWithinDay)
    {
        var timestamp = Day2023Aug01 + offsetWithinDay;

        var day = StatsIngestionTime.ToUtcDay(timestamp);

        day.ShouldBe(Day2023Aug01);
        (day % SecondsPerDay).ShouldBe(0);
    }

    [Fact]
    public void ToUtcDay_of_the_next_midnight_advances_to_the_next_day()
    {
        // The day boundary is inclusive at 00:00:00 UTC and exclusive at the next 00:00:00 UTC, so the very
        // next midnight buckets to the following day rather than the current one.
        var nextMidnight = Day2023Aug01 + SecondsPerDay;

        StatsIngestionTime.ToUtcDay(nextMidnight).ShouldBe(nextMidnight);
        StatsIngestionTime.ToUtcDay(nextMidnight - 1).ShouldBe(Day2023Aug01);
    }

    /// <summary>
    /// In-memory <see cref="IMetricsStore"/> mirroring the production <see cref="MetricsStore"/> semantics
    /// for the members Task 3.7 exercises: counter <c>$inc</c> accumulation, gauge set-semantics,
    /// <c>GetPeriodAsync</c> returning <c>{0,0}</c> when the entity has no recorded metric (else
    /// <c>max_date</c> = most recent recorded day and <c>min_date = max_date - window*86400</c>), and
    /// <c>AggregateAsync</c> summing per-day values over the inclusive range with missing days treated as
    /// <c>0</c>. The remaining members are out of scope for these edge cases.
    /// </summary>
    private sealed class InMemoryMetricsStore : IMetricsStore
    {
        private const int MinReportingWindowDays = 1;
        private const int MaxReportingWindowDays = 365;

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

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays)
        {
            var window = Math.Clamp(reportingWindowDays, MinReportingWindowDays, MaxReportingWindowDays);

            var days = _values.Keys
                .Where(k => k.Entity.Equals(entity))
                .Select(k => k.UtcDay)
                .ToList();

            if (days.Count == 0)
            {
                return Task.FromResult(new StatsDateRange(0, 0));
            }

            var maxDate = days.Max();
            var minDate = maxDate - window * SecondsPerDay;
            return Task.FromResult(new StatsDateRange(minDate, maxDate));
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

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Task 3.7.");

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Task 3.7.");

        public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Task 3.7.");

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            throw new NotSupportedException("Not exercised by Task 3.7.");

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            throw new NotSupportedException("Not exercised by Task 3.7.");
    }
}
