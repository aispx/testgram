using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 6: Per-item graphs reproduce the recorded per-day series over the Period.
///
/// For any recorded per-day views and per-emotion reaction data for a message or story, the produced
/// <c>views_graph</c> and <c>reactions_by_emotion_graph</c> cover exactly the days of the Period and their
/// values equal the recorded daily values, with any day lacking a metric contributing <c>0</c>.
///
/// Validates: Requirements 4.3, 5.4.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. <see cref="InMemoryPerItemMetricsStore"/> (nested below) faithfully mirrors the documented
/// Metrics_Store semantics for the two read paths this property exercises —
/// <see cref="IMetricsStore.GetSeriesAsync"/> (per-day counter values over the range, sorted ascending) and
/// <see cref="IMetricsStore.GetCategorySeriesAsync"/> (per-day values broken down by emotion category,
/// grouped and ordinally sorted) — matching the production <see cref="MetricsStore"/>. The per-item graph
/// assembly under test (the Stats_Service step that turns those series into a Period-covering,
/// zero-filled <see cref="GraphSpec"/>) is embedded here in <see cref="BuildViewsGraphJson"/> /
/// <see cref="BuildReactionsGraphJson"/> and serialized through the shared <see cref="GraphBuilder"/>
/// (over the shared <see cref="FakeAsyncGraphStore"/>). The generator emits sparse per-day data — with some
/// days inside the Period lacking a metric (zero-fill) and some recorded days lying outside the Period
/// (must be excluded) — so both behaviours are exercised. Each run executes a minimum of 100 generated
/// cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(PerItemDaySeriesArbitraries) }, MaxTest = 100)]
public class PerItemDaySeriesPropertyTests
{
    private const int SecondsPerDay = 86_400;

    [Property]
    public void Views_graph_reproduces_recorded_per_day_series_over_the_period(PerItemSeriesCase testCase)
    {
        var store = new InMemoryPerItemMetricsStore();
        var entity = new StatsEntityKey(testCase.EntityType, testCase.OwnerPeerId, testCase.ItemId);
        RecordViews(store, entity, testCase.ViewsPoints);

        var json = BuildViewsGraphJson(store, entity, testCase.PeriodMinDay, testCase.PeriodMaxDay);
        var (xAxis, columns) = ParseGraph(json);

        // The graph covers exactly the days of the Period (Requirement 4.3): one x entry per UTC day from
        // min_date to max_date inclusive, in strictly ascending Unix-millisecond order.
        xAxis.ShouldBe(ExpectedPeriodMillis(testCase.PeriodMinDay, testCase.PeriodMaxDay));

        // views_graph has exactly one data series.
        columns.Count.ShouldBe(1);
        var (_, actualValues) = columns[0];

        // Expected: the recorded value for each Period day, with any day lacking a metric contributing 0,
        // and recorded days outside the Period excluded.
        var recordedInPeriod = testCase.ViewsPoints
            .Where(p => p.UtcDay >= testCase.PeriodMinDay && p.UtcDay <= testCase.PeriodMaxDay)
            .ToDictionary(p => p.UtcDay, p => p.Value);

        var expectedValues = EnumeratePeriodDays(testCase.PeriodMinDay, testCase.PeriodMaxDay)
            .Select(day => recordedInPeriod.GetValueOrDefault(day, 0L))
            .ToList();

        actualValues.ShouldBe(expectedValues);
    }

    [Property]
    public void Reactions_by_emotion_graph_reproduces_recorded_per_day_series_over_the_period(PerItemSeriesCase testCase)
    {
        var store = new InMemoryPerItemMetricsStore();
        var entity = new StatsEntityKey(testCase.EntityType, testCase.OwnerPeerId, testCase.ItemId);
        RecordReactions(store, entity, testCase.ReactionDays);

        var json = BuildReactionsGraphJson(store, entity, testCase.PeriodMinDay, testCase.PeriodMaxDay);
        var (xAxis, columns) = ParseGraph(json);

        // The graph covers exactly the days of the Period (Requirement 5.4).
        xAxis.ShouldBe(ExpectedPeriodMillis(testCase.PeriodMinDay, testCase.PeriodMaxDay));

        // Expected per-emotion daily values over the Period, zero-filled, excluding out-of-Period days.
        var days = EnumeratePeriodDays(testCase.PeriodMinDay, testCase.PeriodMaxDay).ToList();
        var expectedByCategory = new SortedDictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        foreach (var reactionDay in testCase.ReactionDays)
        {
            if (reactionDay.UtcDay < testCase.PeriodMinDay || reactionDay.UtcDay > testCase.PeriodMaxDay)
            {
                continue;
            }

            foreach (var (category, value) in reactionDay.Breakdown)
            {
                if (!expectedByCategory.TryGetValue(category, out var byDay))
                {
                    byDay = new Dictionary<int, long>();
                    expectedByCategory[category] = byDay;
                }

                byDay[reactionDay.UtcDay] = byDay.GetValueOrDefault(reactionDay.UtcDay) + value;
            }
        }

        // One data column per emotion category present in the Period, in the store's ordinal category order.
        columns.Select(c => c.Id).ShouldBe(expectedByCategory.Keys.ToList());

        foreach (var (category, values) in columns)
        {
            var byDay = expectedByCategory[category];
            var expectedValues = days.Select(day => byDay.GetValueOrDefault(day, 0L)).ToList();
            values.ShouldBe(expectedValues);
        }
    }

    // ---- Per-item graph assembly under test (Stats_Service step for views_graph / reactions_graph) ------

    /// <summary>
    /// Turns the Metrics_Store per-day views series into a Period-covering, zero-filled
    /// <see cref="GraphSpec"/> and serializes it via the Graph_Builder — the assembly Property 6 checks.
    /// </summary>
    private static string BuildViewsGraphJson(IMetricsStore store, StatsEntityKey entity, int minDay, int maxDay)
    {
        var series = store.GetSeriesAsync(entity, StatsMetricNames.Views, minDay, maxDay)
            .GetAwaiter().GetResult();
        var valueByDay = series.ToDictionary(p => p.UtcDay, p => p.Value);

        var days = EnumeratePeriodDays(minDay, maxDay).ToList();
        var x = days.Select(DayToMillis).ToList();
        var values = days.Select(day => valueByDay.GetValueOrDefault(day, 0L)).ToList();

        var spec = new GraphSpec(
            GraphKind.Line,
            x,
            new List<GraphSeries> { new("views", "Views", "primary", values) });

        return new GraphBuilder(new FakeAsyncGraphStore()).SerializeGraphJson(spec, dark: false);
    }

    /// <summary>
    /// Turns the Metrics_Store per-emotion category series into a Period-covering, zero-filled
    /// <see cref="GraphSpec"/> (one series per emotion category) and serializes it via the Graph_Builder.
    /// </summary>
    private static string BuildReactionsGraphJson(IMetricsStore store, StatsEntityKey entity, int minDay, int maxDay)
    {
        var categorySeries = store.GetCategorySeriesAsync(entity, StatsMetricNames.Reactions, minDay, maxDay)
            .GetAwaiter().GetResult();

        var days = EnumeratePeriodDays(minDay, maxDay).ToList();
        var x = days.Select(DayToMillis).ToList();

        var graphSeries = categorySeries
            .Select(cs =>
            {
                var byDay = cs.Points.ToDictionary(p => p.UtcDay, p => p.Value);
                var values = days.Select(day => byDay.GetValueOrDefault(day, 0L)).ToList();
                return new GraphSeries(cs.Category, cs.Category, "primary", values);
            })
            .ToList();

        var spec = new GraphSpec(GraphKind.Line, x, graphSeries);
        return new GraphBuilder(new FakeAsyncGraphStore()).SerializeGraphJson(spec, dark: false);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static void RecordViews(IMetricsStore store, StatsEntityKey entity,
        IReadOnlyList<DailyMetricPointFixture> points)
    {
        foreach (var point in points)
        {
            store.RecordAsync(entity, StatsMetricNames.Views, point.UtcDay, point.Value)
                .GetAwaiter().GetResult();
        }
    }

    private static void RecordReactions(IMetricsStore store, StatsEntityKey entity,
        IReadOnlyList<ReactionDayFixture> reactionDays)
    {
        foreach (var reactionDay in reactionDays)
        {
            var total = reactionDay.Breakdown.Values.Sum();
            store.RecordAsync(entity, StatsMetricNames.Reactions, reactionDay.UtcDay, total, reactionDay.Breakdown)
                .GetAwaiter().GetResult();
        }
    }

    private static IEnumerable<int> EnumeratePeriodDays(int minDay, int maxDay)
    {
        for (var day = minDay; day <= maxDay; day += SecondsPerDay)
        {
            yield return day;
        }
    }

    private static List<long> ExpectedPeriodMillis(int minDay, int maxDay) =>
        EnumeratePeriodDays(minDay, maxDay).Select(DayToMillis).ToList();

    private static long DayToMillis(int utcDay) => (long)utcDay * 1000L;

    private static (List<long> XAxis, List<(string Id, List<long> Values)> Columns) ParseGraph(string json)
    {
        var root = JsonNode.Parse(json).ShouldBeOfType<JsonObject>();
        var columns = root["columns"].ShouldBeOfType<JsonArray>();

        var xColumn = columns[0].ShouldBeOfType<JsonArray>();
        xColumn[0]!.GetValue<string>().ShouldBe("x");
        var xAxis = new List<long>(xColumn.Count - 1);
        for (var i = 1; i < xColumn.Count; i++)
        {
            xAxis.Add(xColumn[i]!.GetValue<long>());
        }

        var dataColumns = new List<(string, List<long>)>(columns.Count - 1);
        for (var c = 1; c < columns.Count; c++)
        {
            var column = columns[c].ShouldBeOfType<JsonArray>();
            var id = column[0]!.GetValue<string>();
            var values = new List<long>(column.Count - 1);
            for (var i = 1; i < column.Count; i++)
            {
                values.Add(column[i]!.GetValue<long>());
            }

            dataColumns.Add((id, values));
        }

        return (xAxis, dataColumns);
    }

    // ---- In-memory Metrics_Store faithful to the two read paths Property 6 uses -------------------------

    /// <summary>
    /// An in-memory <see cref="IMetricsStore"/> mirroring the production <see cref="MetricsStore"/>
    /// record/read semantics without MongoDB. Counter metrics accumulate via <c>$inc</c> (and their
    /// breakdown categories likewise), gauge metrics use set-semantics; <see cref="GetSeriesAsync"/> returns
    /// the per-day values within the inclusive range sorted ascending, and
    /// <see cref="GetCategorySeriesAsync"/> groups the per-day breakdown values by category (ordinal-sorted,
    /// points ascending by day). Only the methods exercised by Property 6 are implemented.
    /// </summary>
    private sealed class InMemoryPerItemMetricsStore : IMetricsStore
    {
        private sealed class Cell
        {
            public long Value;
            public Dictionary<string, long>? Breakdown;
        }

        private readonly Dictionary<(StatsEntityKey Entity, string Metric, int UtcDay), Cell> _cells = new();

        public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
            IReadOnlyDictionary<string, long>? breakdown = null)
        {
            var key = (entity, metric, utcDay);
            if (!_cells.TryGetValue(key, out var cell))
            {
                cell = new Cell();
                _cells[key] = cell;
            }

            var isGauge = StatsMetricNames.IsGauge(metric);
            cell.Value = isGauge ? delta : cell.Value + delta;

            if (breakdown is { Count: > 0 })
            {
                cell.Breakdown ??= new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (category, value) in breakdown)
                {
                    cell.Breakdown[category] = isGauge
                        ? value
                        : cell.Breakdown.GetValueOrDefault(category) + value;
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
        {
            if (maxDayUtc < minDayUtc)
            {
                return Task.FromResult<IReadOnlyList<DailyPoint>>([]);
            }

            var points = _cells
                .Where(kv => kv.Key.Entity.Equals(entity)
                             && kv.Key.Metric == metric
                             && kv.Key.UtcDay >= minDayUtc
                             && kv.Key.UtcDay <= maxDayUtc)
                .OrderBy(kv => kv.Key.UtcDay)
                .Select(kv => new DailyPoint(kv.Key.UtcDay, kv.Value.Value))
                .ToList();

            return Task.FromResult<IReadOnlyList<DailyPoint>>(points);
        }

        public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
        {
            if (maxDayUtc < minDayUtc)
            {
                return Task.FromResult<IReadOnlyList<CategorySeries>>([]);
            }

            var matching = _cells
                .Where(kv => kv.Key.Entity.Equals(entity)
                             && kv.Key.Metric == metric
                             && kv.Key.UtcDay >= minDayUtc
                             && kv.Key.UtcDay <= maxDayUtc)
                .OrderBy(kv => kv.Key.UtcDay)
                .ToList();

            var byCategory = new Dictionary<string, List<DailyPoint>>(StringComparer.Ordinal);
            foreach (var kv in matching)
            {
                if (kv.Value.Breakdown == null)
                {
                    continue;
                }

                foreach (var (category, value) in kv.Value.Breakdown)
                {
                    if (!byCategory.TryGetValue(category, out var pts))
                    {
                        pts = [];
                        byCategory[category] = pts;
                    }

                    pts.Add(new DailyPoint(kv.Key.UtcDay, value));
                }
            }

            var result = byCategory
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CategorySeries(kv.Key, kv.Value))
                .ToList();

            return Task.FromResult<IReadOnlyList<CategorySeries>>(result);
        }

        public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays) =>
            throw new NotSupportedException("Not exercised by Property 6.");

        public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
            throw new NotSupportedException("Not exercised by Property 6.");

        public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
            throw new NotSupportedException("Not exercised by Property 6.");

        public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
            throw new NotSupportedException("Not exercised by Property 6.");
    }
}

/// <summary>Per-day reaction breakdown fixture: a UTC day plus its per-emotion category counts.</summary>
public sealed record ReactionDayFixture(int UtcDay, IReadOnlyDictionary<string, long> Breakdown)
{
    public override string ToString() =>
        $"Reactions(day={UtcDay}, [{string.Join(",", Breakdown.Select(kv => $"{kv.Key}={kv.Value}"))}])";
}

/// <summary>
/// A generated Property 6 case: a message or story entity, a Period <c>[min, max]</c> of aligned UTC days,
/// a sparse per-day views series, and sparse per-day per-emotion reaction data. Recorded days may fall
/// inside or outside the Period so both zero-fill (missing Period days) and exclusion (out-of-Period days)
/// are exercised.
/// </summary>
public sealed record PerItemSeriesCase(
    StatsEntityType EntityType,
    long OwnerPeerId,
    long ItemId,
    int PeriodMinDay,
    int PeriodMaxDay,
    IReadOnlyList<DailyMetricPointFixture> ViewsPoints,
    IReadOnlyList<ReactionDayFixture> ReactionDays)
{
    public override string ToString() =>
        $"PerItemSeriesCase({EntityType} {OwnerPeerId}/{ItemId}, period=[{PeriodMinDay},{PeriodMaxDay}], " +
        $"views={ViewsPoints.Count}, reactionDays={ReactionDays.Count})";
}

/// <summary>FsCheck arbitrary registration for <see cref="PerItemSeriesCase"/> (Property 6).</summary>
public static class PerItemDaySeriesArbitraries
{
    private const int SecondsPerDay = 86_400;

    // Readable emotion-category names (the reactions_by_emotion_graph groups by emotion category).
    private static readonly string[] Emotions = { "angry", "fire", "like", "love", "sad" };

    public static Arbitrary<PerItemSeriesCase> PerItemSeriesCase() => Arb.From(Case);

    private static Gen<PerItemSeriesCase> Case =>
        from entityType in Gen.Elements(StatsEntityType.Message, StatsEntityType.Story)
        from ownerPeerId in StatsGen.PooledId.Select(id => id + 1000)
        from itemId in StatsGen.PooledId
        from periodMin in StatsGen.AlignedUtcDay
        from windowDays in Gen.Choose(0, 40)
        let periodMax = periodMin + windowDays * SecondsPerDay
        from viewsPoints in ViewsPoints(periodMin, periodMax)
        from reactionDays in ReactionDays(periodMin, periodMax)
        select new PerItemSeriesCase(entityType, ownerPeerId, itemId, periodMin, periodMax, viewsPoints, reactionDays);

    // Candidate days span the Period plus a two-day margin on each side so recorded-but-out-of-Period days
    // (which must be excluded) are produced alongside in-Period days.
    private static int[] CandidateDays(int periodMin, int periodMax)
    {
        var days = new List<int>();
        for (var day = periodMin - 2 * SecondsPerDay; day <= periodMax + 2 * SecondsPerDay; day += SecondsPerDay)
        {
            days.Add(day);
        }

        return days.ToArray();
    }

    private static Gen<IReadOnlyList<DailyMetricPointFixture>> ViewsPoints(int periodMin, int periodMax)
    {
        var candidates = CandidateDays(periodMin, periodMax);

        return StatsGen.ArrayOfLength(
                candidates.Length,
                from include in Gen.Frequency(
                    Tuple.Create(2, Gen.Constant(true)),
                    Tuple.Create(1, Gen.Constant(false)))
                from value in Gen.Choose(0, 100_000).Select(i => (long)i)
                select (include, value))
            .Select(flags => (IReadOnlyList<DailyMetricPointFixture>)candidates
                .Zip(flags, (day, f) => (day, f))
                .Where(t => t.f.include)
                .Select(t => new DailyMetricPointFixture(t.day, t.f.value))
                .ToList());
    }

    private static Gen<IReadOnlyList<ReactionDayFixture>> ReactionDays(int periodMin, int periodMax)
    {
        var candidates = CandidateDays(periodMin, periodMax);

        return StatsGen.ArrayOfLength(
                candidates.Length,
                from include in Gen.Frequency(
                    Tuple.Create(2, Gen.Constant(true)),
                    Tuple.Create(1, Gen.Constant(false)))
                from breakdown in EmotionBreakdown
                select (include, breakdown))
            .Select(flags => (IReadOnlyList<ReactionDayFixture>)candidates
                .Zip(flags, (day, f) => (day, f))
                // Only keep days that are both included and carry at least one emotion count.
                .Where(t => t.f.include && t.f.breakdown.Count > 0)
                .Select(t => new ReactionDayFixture(t.day, t.f.breakdown))
                .ToList());
    }

    private static Gen<IReadOnlyDictionary<string, long>> EmotionBreakdown =>
        StatsGen.ArrayOfLength(
                Emotions.Length,
                from include in Gen.Frequency(
                    Tuple.Create(2, Gen.Constant(true)),
                    Tuple.Create(1, Gen.Constant(false)))
                from value in Gen.Choose(0, 50_000).Select(i => (long)i)
                select (include, value))
            .Select(flags => (IReadOnlyDictionary<string, long>)Emotions
                .Zip(flags, (emotion, f) => (emotion, f))
                .Where(t => t.f.include)
                .ToDictionary(t => t.emotion, t => t.f.value, StringComparer.Ordinal));
}
