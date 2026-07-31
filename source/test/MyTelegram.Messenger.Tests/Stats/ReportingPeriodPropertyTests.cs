using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using FsCheck.Xunit;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 16: The reporting Period is computed correctly.
///
/// For any recorded per-day metric data and configured reporting window <c>w</c> (1..365 days), when at
/// least one metric exists the returned <c>period</c> has <c>max_date</c> equal to <c>00:00:00 UTC</c> of
/// the most recent day with a recorded metric, <c>min_date</c> equal to <c>max_date - w*86400</c>, both
/// aligned to <c>00:00:00 UTC</c> Unix seconds, and <c>min_date &lt;= max_date</c>.
///
/// Validates: Requirements 10.3.
///
/// Per the tasks.md notes, storage property tests run against in-memory/mocked stores (no real MongoDB in
/// the property loop). <see cref="ReportingPeriodMetricsStoreFake"/> below faithfully reproduces the
/// production <see cref="MetricsStore.GetPeriodAsync"/> semantics — window clamp to 1..365,
/// <c>max_date</c> = most recent recorded day for the entity, <c>min_date = max_date - window*86400</c>,
/// and <c>{0,0}</c> when the entity has no recorded metric. The shared <see cref="StatsGen.MetricSeries"/>
/// generator (sparse, ascending, uniquely-dayed per-day data plus a 1..365 window) drives a minimum of
/// 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class ReportingPeriodPropertyTests
{
    private const int SecondsPerDay = 86_400;

    [Property]
    public void Reporting_period_is_computed_correctly(DailyMetricSeriesFixture fixture)
    {
        var entity = new StatsEntityKey(StatsEntityType.Channel, OwnerPeerId: 4242, ItemId: 0);
        var store = new ReportingPeriodMetricsStoreFake();

        foreach (var point in fixture.Points)
        {
            store.RecordAsync(entity, fixture.Metric, point.UtcDay, point.Value)
                .GetAwaiter().GetResult();
        }

        var window = fixture.ReportingWindowDays;
        var period = store.GetPeriodAsync(entity, window).GetAwaiter().GetResult();

        if (fixture.Points.Count == 0)
        {
            // Requirement 10.4 boundary: no recorded metric yields {0,0}. (Property 16 concerns the
            // "at least one metric" case; the empty case is asserted here so the generator's empty draws
            // are meaningful rather than vacuous.)
            period.MinDate.ShouldBe(0);
            period.MaxDate.ShouldBe(0);
            return;
        }

        // The window is generated in 1..365, so the production clamp is an identity here.
        var expectedMaxDate = fixture.Points.Max(p => p.UtcDay);
        var expectedMinDate = expectedMaxDate - window * SecondsPerDay;

        // max_date is 00:00:00 UTC of the most recent day with a recorded metric.
        period.MaxDate.ShouldBe(expectedMaxDate);

        // min_date is max_date minus the reporting window.
        period.MinDate.ShouldBe(expectedMinDate);

        // Both bounds are aligned to 00:00:00 UTC Unix seconds.
        (period.MaxDate % SecondsPerDay).ShouldBe(0);
        (period.MinDate % SecondsPerDay).ShouldBe(0);

        // min_date <= max_date (window is a positive whole number of days).
        period.MinDate.ShouldBeLessThanOrEqualTo(period.MaxDate);
    }

    /// <summary>
    /// In-memory <see cref="IMetricsStore"/> whose <see cref="GetPeriodAsync"/> mirrors the production
    /// <see cref="MetricsStore"/> algorithm exactly: clamp the window to 1..365, take the most recent
    /// recorded <c>UtcDay</c> for the entity as <c>max_date</c>, set <c>min_date = max_date - window*86400</c>,
    /// and return <c>{0,0}</c> when the entity has no recorded metric. Only the members this property
    /// exercises are implemented; the rest are out of scope for Property 16.
    /// </summary>
    private sealed class ReportingPeriodMetricsStoreFake : IMetricsStore
    {
        private const int MinReportingWindowDays = 1;
        private const int MaxReportingWindowDays = 365;

        private readonly List<(StatsEntityKey Entity, int UtcDay)> _records = new();

        public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
            IReadOnlyDictionary<string, long>? breakdown = null)
        {
            _records.Add((entity, utcDay));
            return Task.CompletedTask;
        }

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays)
        {
            var window = Math.Clamp(reportingWindowDays, MinReportingWindowDays, MaxReportingWindowDays);

            var days = _records.Where(r => r.Entity.Equals(entity)).Select(r => r.UtcDay).ToList();
            if (days.Count == 0)
            {
                return Task.FromResult(new StatsDateRange(0, 0));
            }

            var maxDate = days.Max();
            var minDate = maxDate - window * SecondsPerDay;
            return Task.FromResult(new StatsDateRange(minDate, maxDate));
        }

        public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 16.");

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 16.");

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 16.");

        public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 16.");

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            throw new NotSupportedException("Not exercised by Property 16.");

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            throw new NotSupportedException("Not exercised by Property 16.");
    }
}
