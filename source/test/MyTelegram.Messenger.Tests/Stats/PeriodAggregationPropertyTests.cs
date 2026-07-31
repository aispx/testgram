using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 15: Period aggregates equal the sum of per-day metrics for counters and
/// the most recent snapshot for gauges.
///
/// For any recorded per-day metric data and any <c>[min_date, max_date]</c> range, the aggregate returned
/// by the Metrics_Store equals: for counter metrics, the sum of the per-day values for every day in the
/// range (days with no recorded metric contributing <c>0</c>); for gauge metrics (absolute snapshots such
/// as followers), the most recent snapshot recorded at or before the range end — summing daily snapshots
/// would multiply the absolute value by the number of recorded days.
///
/// Validates: Requirements 10.2, 10.5, 10.6.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. <see cref="InMemoryMetricsStore"/> (nested below) faithfully mirrors the documented
/// Metrics_Store semantics — <c>$inc</c> accumulation for counter metrics, set-semantics for the
/// absolute-gauge family, sum-with-zero-fill for counter <c>AggregateAsync</c>, latest-snapshot for gauge
/// <c>AggregateAsync</c> — matching the production <see cref="MetricsStore"/>. The shared
/// <see cref="StatsGen.MetricSeries"/> generator emits sparse day data (unique, ascending days with gaps)
/// and the local range generator picks endpoints that land on and between recorded days, so the zero-fill
/// behaviour (missing day => 0) is exercised. Each run executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(PeriodAggregationArbitraries) }, MaxTest = 100)]
public class PeriodAggregationPropertyTests
{
    [Property]
    public void Period_aggregate_equals_sum_of_per_day_metrics(PeriodAggregationCase testCase)
    {
        var store = new InMemoryMetricsStore();
        var entity = new StatsEntityKey(StatsEntityType.Channel, 42, 0);
        var series = testCase.Series;

        // Record the sparse per-day data through the store's write path (idempotent per (entity, metric, day)).
        foreach (var point in series.Points)
        {
            store.RecordAsync(entity, series.Metric, point.UtcDay, point.Value).GetAwaiter().GetResult();
        }

        // Expected: for counters, the sum of the per-day values within the inclusive range (days with no
        // recorded metric contribute 0 by not appearing in the recorded set, 10.5); for gauges, the most
        // recent snapshot at or before the range end (the lower bound is ignored — a gauge that last
        // changed before the window still holds that value throughout it).
        var expected = StatsMetricNames.IsGauge(series.Metric)
            ? series.Points
                .Where(p => p.UtcDay <= testCase.MaxDayUtc)
                .OrderBy(p => p.UtcDay)
                .Select(p => p.Value)
                .LastOrDefault()
            : series.Points
                .Where(p => p.UtcDay >= testCase.MinDayUtc && p.UtcDay <= testCase.MaxDayUtc)
                .Sum(p => p.Value);

        var actual = store
            .AggregateAsync(entity, series.Metric, testCase.MinDayUtc, testCase.MaxDayUtc)
            .GetAwaiter()
            .GetResult();

        // Requirement 10.6: counter aggregates equal the sum over the range; gauge aggregates equal the
        // latest snapshot.
        actual.ShouldBe(expected);
    }

    /// <summary>
    /// A generated case: a sparse per-day metric series paired with an inclusive <c>[min, max]</c> day range
    /// whose endpoints are drawn on and around the recorded days so ranges frequently cover recorded days,
    /// span gaps (zero-fill), and exclude days beyond the data.
    /// </summary>
    public sealed record PeriodAggregationCase(DailyMetricSeriesFixture Series, int MinDayUtc, int MaxDayUtc)
    {
        public override string ToString() =>
            $"PeriodAggregationCase({Series}, range=[{MinDayUtc}, {MaxDayUtc}])";
    }

    /// <summary>FsCheck arbitrary registration for <see cref="PeriodAggregationCase"/>.</summary>
    public static class PeriodAggregationArbitraries
    {
        private const int SecondsPerDay = 86_400;

        public static Arbitrary<PeriodAggregationCase> PeriodAggregationCase() => Arb.From(Case);

        private static Gen<PeriodAggregationCase> Case =>
            from series in StatsGen.MetricSeries
            from a in RangeEndpoint(series)
            from b in RangeEndpoint(series)
            select new PeriodAggregationCase(series, Math.Min(a, b), Math.Max(a, b));

        // Range endpoints are drawn from the recorded days and the days immediately before/after each (to
        // land exactly on inclusive bounds and one day into the gaps), so both full-coverage and partial /
        // zero-fill ranges are produced. When the series is empty, endpoints are arbitrary aligned days and
        // the expected sum is 0 for every range.
        private static Gen<int> RangeEndpoint(DailyMetricSeriesFixture series)
        {
            if (series.Points.Count == 0)
            {
                return StatsGen.AlignedUtcDay;
            }

            var candidates = new List<int>(series.Points.Count * 3);
            foreach (var point in series.Points)
            {
                candidates.Add(point.UtcDay - SecondsPerDay);
                candidates.Add(point.UtcDay);
                candidates.Add(point.UtcDay + SecondsPerDay);
            }

            return Gen.Elements(candidates.ToArray());
        }
    }

    /// <summary>
    /// An in-memory <see cref="IMetricsStore"/> that faithfully mirrors the production
    /// <see cref="MetricsStore"/> record/aggregate semantics without MongoDB: counter metrics accumulate
    /// via <c>$inc</c>, absolute-gauge metrics use set-semantics, and <see cref="AggregateAsync"/> sums the
    /// per-day values across the inclusive range with missing days treated as 0. Only the methods exercised
    /// by Property 15 are implemented; the list/period helpers are out of scope here and are covered by
    /// their own property/unit tasks.
    /// </summary>
    private sealed class InMemoryMetricsStore : IMetricsStore
    {
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
                // Mirrors production: the most recent snapshot at or before the range end; the lower
                // bound is deliberately ignored.
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

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays) =>
            throw new NotSupportedException("Not exercised by Property 15.");

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 15.");

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 15.");

        public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 15.");

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            throw new NotSupportedException("Not exercised by Property 15.");

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            throw new NotSupportedException("Not exercised by Property 15.");
    }
}
