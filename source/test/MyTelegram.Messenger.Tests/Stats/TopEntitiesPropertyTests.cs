using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 4: Top-entity lists are bounded, sorted, and consistent with <c>users</c>.
///
/// For any supergroup activity data, each of <c>top_posters</c>, <c>top_admins</c>, and
/// <c>top_inviters</c> contains at most 10 entries ordered in non-increasing order of the activity count
/// it measures, and <c>users</c> contains exactly one entry for each distinct user id referenced by those
/// three lists (no duplicates, full coverage).
///
/// Validates: Requirements 3.3, 3.5.
///
/// Per the tasks.md notes, storage property tests run against in-memory/mocked stores (no real MongoDB in
/// the property loop). The production <see cref="MetricsStore"/> is MongoDB-backed, so
/// <see cref="TopEntitiesInMemoryMetricsStore"/> below faithfully reproduces the documented semantics of
/// <see cref="IMetricsStore.GetTopEntitiesAsync"/>: aggregate the per-user breakdowns for
/// posters/admins/inviters via the <c>StatsMetricNames.TopPoster*/TopAdmin*/TopInviter*</c> metrics over
/// the requested day range (counter <c>$inc</c> accumulation), build one entry per referenced user id,
/// sort descending by the activity count each list measures, cap each list at <c>perListMax</c> (default
/// 10), and return the distinct set of user ids referenced across the three capped lists. Each run
/// executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(TopEntitiesArbitraries) }, MaxTest = 100)]
public class TopEntitiesPropertyTests
{
    [Property]
    public void Top_entity_lists_are_bounded_sorted_and_consistent_with_users(TopEntitiesFixture fixture)
    {
        var store = new TopEntitiesInMemoryMetricsStore();
        var channel = new StatsEntityKey(StatsEntityType.Channel, fixture.ChannelId, 0);

        // Seed the per-day, per-user breakdown records through the store's write path so accumulation
        // across multiple days is exercised (counter $inc semantics per (entity, metric, day)).
        foreach (var record in fixture.Records)
        {
            store.RecordAsync(
                    channel,
                    record.Metric,
                    record.UtcDay,
                    record.Values.Values.Sum(),
                    record.Values.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value, StringComparer.Ordinal))
                .GetAwaiter().GetResult();
        }

        var top = store
            .GetTopEntitiesAsync(fixture.ChannelId, fixture.MinDayUtc, fixture.MaxDayUtc, fixture.PerListMax)
            .GetAwaiter().GetResult();

        var cap = fixture.PerListMax <= 0 ? 0 : fixture.PerListMax;

        // --- Bounded: each list holds at most the requested cap (design property speaks of 10). ---
        top.Posters.Count.ShouldBeLessThanOrEqualTo(cap);
        top.Admins.Count.ShouldBeLessThanOrEqualTo(cap);
        top.Inviters.Count.ShouldBeLessThanOrEqualTo(cap);

        // --- Sorted: non-increasing by the activity count each list measures. ---
        AssertNonIncreasing(top.Posters.Select(p => (long)p.Messages));
        AssertNonIncreasing(top.Admins.Select(a => (long)a.Deleted + a.Kicked + a.Banned));
        AssertNonIncreasing(top.Inviters.Select(i => (long)i.Invitations));

        // --- Cap selection keeps the highest: no excluded user outranks an included one. ---
        AssertTopSelection(
            cap,
            top.Posters.Select(p => (p.UserId, (long)p.Messages)).ToList(),
            PosterTotals(fixture));
        AssertTopSelection(
            cap,
            top.Admins.Select(a => (a.UserId, (long)a.Deleted + a.Kicked + a.Banned)).ToList(),
            AdminTotals(fixture));
        AssertTopSelection(
            cap,
            top.Inviters.Select(i => (i.UserId, (long)i.Invitations)).ToList(),
            InviterTotals(fixture));

        // --- Consistency with users: exactly one entry per distinct referenced user id. ---
        var referenced = top.Posters.Select(p => p.UserId)
            .Concat(top.Admins.Select(a => a.UserId))
            .Concat(top.Inviters.Select(i => i.UserId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        // No duplicates in the users list.
        top.UserIds.Count.ShouldBe(top.UserIds.Distinct().Count());
        // Full coverage and nothing extraneous: users == distinct union of the three lists' user ids.
        top.UserIds.OrderBy(id => id).ShouldBe(referenced);
    }

    private static void AssertNonIncreasing(IEnumerable<long> measures)
    {
        var list = measures.ToList();
        for (var i = 1; i < list.Count; i++)
        {
            list[i - 1].ShouldBeGreaterThanOrEqualTo(list[i]);
        }
    }

    // Every user included in the capped list must have a measure >= every user that was left out; and the
    // number of included entries equals min(cap, number of users with any recorded activity in range).
    private static void AssertTopSelection(
        int cap,
        IReadOnlyList<(long UserId, long Measure)> selected,
        Dictionary<long, long> allTotals)
    {
        selected.Count.ShouldBe(Math.Min(cap, allTotals.Count));

        // The selected entries' measures match the independently-computed totals.
        foreach (var (userId, measure) in selected)
        {
            measure.ShouldBe(allTotals[userId]);
        }

        if (cap == 0)
        {
            return;
        }

        var selectedIds = selected.Select(s => s.UserId).ToHashSet();
        var minSelectedMeasure = selected.Count > 0 ? selected.Min(s => s.Measure) : long.MaxValue;
        foreach (var (userId, total) in allTotals)
        {
            if (!selectedIds.Contains(userId))
            {
                // Any excluded user cannot outrank the weakest included user.
                total.ShouldBeLessThanOrEqualTo(minSelectedMeasure);
            }
        }
    }

    // Independently aggregate the fixture's per-user totals over the requested range, mirroring the
    // store's range filter (UtcDay in [min, max]) but computed straight from the raw fixture data.
    private static Dictionary<long, long> AggregateInRange(TopEntitiesFixture fixture, params string[] metrics)
    {
        var wanted = metrics.ToHashSet(StringComparer.Ordinal);
        var totals = new Dictionary<long, long>();
        if (fixture.MaxDayUtc < fixture.MinDayUtc)
        {
            return totals;
        }

        foreach (var record in fixture.Records)
        {
            if (!wanted.Contains(record.Metric) ||
                record.UtcDay < fixture.MinDayUtc ||
                record.UtcDay > fixture.MaxDayUtc)
            {
                continue;
            }

            foreach (var (userId, value) in record.Values)
            {
                totals[userId] = totals.GetValueOrDefault(userId) + value;
            }
        }

        return totals;
    }

    private static Dictionary<long, long> PosterTotals(TopEntitiesFixture fixture) =>
        AggregateInRange(fixture, StatsMetricNames.TopPosterMessages);

    private static Dictionary<long, long> AdminTotals(TopEntitiesFixture fixture)
    {
        var deleted = AggregateInRange(fixture, StatsMetricNames.TopAdminDeleted);
        var kicked = AggregateInRange(fixture, StatsMetricNames.TopAdminKicked);
        var banned = AggregateInRange(fixture, StatsMetricNames.TopAdminBanned);
        var totals = new Dictionary<long, long>();
        foreach (var userId in deleted.Keys.Union(kicked.Keys).Union(banned.Keys))
        {
            totals[userId] = deleted.GetValueOrDefault(userId)
                             + kicked.GetValueOrDefault(userId)
                             + banned.GetValueOrDefault(userId);
        }

        return totals;
    }

    private static Dictionary<long, long> InviterTotals(TopEntitiesFixture fixture) =>
        AggregateInRange(fixture, StatsMetricNames.TopInviterInvitations);
}

/// <summary>
/// In-memory <see cref="IMetricsStore"/> faithful to the documented top-entities semantics of the
/// MongoDB-backed <see cref="MetricsStore"/>. Only <see cref="RecordAsync"/> and
/// <see cref="GetTopEntitiesAsync"/> are needed for Property 4; the remaining members are not exercised.
/// </summary>
internal sealed class TopEntitiesInMemoryMetricsStore : IMetricsStore
{
    // Keyed by (entity, metric, utcDay); each cell carries the scalar value and a per-category breakdown
    // (category = user id string), mirroring the stats_metrics_daily document.
    private readonly Dictionary<string, MetricCell> _cells = new(StringComparer.Ordinal);

    private sealed class MetricCell
    {
        public long Value { get; set; }
        public Dictionary<string, long> Breakdown { get; } = new(StringComparer.Ordinal);
    }

    public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
        IReadOnlyDictionary<string, long>? breakdown = null)
    {
        var id = $"{(int)entity.Type}:{entity.OwnerPeerId}:{entity.ItemId}:{metric}:{utcDay}";
        if (!_cells.TryGetValue(id, out var cell))
        {
            cell = new MetricCell();
            _cells[id] = cell;
        }

        var isGauge = StatsMetricNames.IsGauge(metric);
        cell.Value = isGauge ? delta : cell.Value + delta;

        if (breakdown is { Count: > 0 })
        {
            foreach (var (category, value) in breakdown)
            {
                cell.Breakdown[category] = isGauge
                    ? value
                    : cell.Breakdown.GetValueOrDefault(category) + value;
            }
        }

        return Task.CompletedTask;
    }

    public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10)
    {
        var channel = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var cap = perListMax <= 0 ? 0 : perListMax;

        var posterMessages = AggregateBreakdown(channel, StatsMetricNames.TopPosterMessages, minDayUtc, maxDayUtc);
        var posterChars = AggregateBreakdown(channel, StatsMetricNames.TopPosterChars, minDayUtc, maxDayUtc);
        var adminDeleted = AggregateBreakdown(channel, StatsMetricNames.TopAdminDeleted, minDayUtc, maxDayUtc);
        var adminKicked = AggregateBreakdown(channel, StatsMetricNames.TopAdminKicked, minDayUtc, maxDayUtc);
        var adminBanned = AggregateBreakdown(channel, StatsMetricNames.TopAdminBanned, minDayUtc, maxDayUtc);
        var inviterInvitations = AggregateBreakdown(channel, StatsMetricNames.TopInviterInvitations, minDayUtc, maxDayUtc);

        var posters = posterMessages.Keys
            .Select(userId =>
            {
                var messages = posterMessages[userId];
                posterChars.TryGetValue(userId, out var chars);
                var avgChars = messages > 0 ? (int)(chars / messages) : 0;
                return new TopPoster(userId, (int)messages, avgChars);
            })
            .OrderByDescending(p => p.Messages)
            .ThenByDescending(p => p.UserId)
            .Take(cap)
            .ToList();

        var adminUserIds = adminDeleted.Keys.Union(adminKicked.Keys).Union(adminBanned.Keys);
        var admins = adminUserIds
            .Select(userId =>
            {
                adminDeleted.TryGetValue(userId, out var deleted);
                adminKicked.TryGetValue(userId, out var kicked);
                adminBanned.TryGetValue(userId, out var banned);
                return new TopAdmin(userId, (int)deleted, (int)kicked, (int)banned);
            })
            .OrderByDescending(a => (long)a.Deleted + a.Kicked + a.Banned)
            .ThenByDescending(a => a.UserId)
            .Take(cap)
            .ToList();

        var inviters = inviterInvitations.Keys
            .Select(userId => new TopInviter(userId, (int)inviterInvitations[userId]))
            .OrderByDescending(i => i.Invitations)
            .ThenByDescending(i => i.UserId)
            .Take(cap)
            .ToList();

        var userIds = posters.Select(p => p.UserId)
            .Concat(admins.Select(a => a.UserId))
            .Concat(inviters.Select(i => i.UserId))
            .Distinct()
            .ToList();

        return Task.FromResult(new TopEntities(posters, admins, inviters, userIds));
    }

    private Dictionary<long, long> AggregateBreakdown(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        var totals = new Dictionary<long, long>();
        if (maxDayUtc < minDayUtc)
        {
            return totals;
        }

        foreach (var kv in _cells)
        {
            var parts = kv.Key.Split(':');
            // key format: "{type}:{owner}:{item}:{metric}:{utcDay}"
            var cellType = int.Parse(parts[0]);
            var owner = long.Parse(parts[1]);
            var item = long.Parse(parts[2]);
            var cellMetric = parts[3];
            var utcDay = int.Parse(parts[4]);

            if (cellType != (int)entity.Type || owner != entity.OwnerPeerId || item != entity.ItemId ||
                cellMetric != metric || utcDay < minDayUtc || utcDay > maxDayUtc)
            {
                continue;
            }

            foreach (var (category, value) in kv.Value.Breakdown)
            {
                if (!long.TryParse(category, out var userId))
                {
                    continue;
                }

                totals[userId] = totals.GetValueOrDefault(userId) + value;
            }
        }

        return totals;
    }

    public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta) =>
        RecordAsync(entity, metric, utcDay, delta, null);

    public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays) =>
        throw new NotSupportedException("Not exercised by Property 4.");

    public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException("Not exercised by Property 4.");

    public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException("Not exercised by Property 4.");

    public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException("Not exercised by Property 4.");

    public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException("Not exercised by Property 4.");

    public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100) =>
        throw new NotSupportedException("Not exercised by Property 4.");
}

/// <summary>
/// One recorded per-day breakdown row for a top-entity metric: the metric name, the UTC day key, and a
/// map of user id -> activity value contributed on that day.
/// </summary>
public sealed record TopEntityRecordFixture(string Metric, int UtcDay, IReadOnlyDictionary<long, long> Values)
{
    public override string ToString() => $"{Metric}@{UtcDay}(users={Values.Count})";
}

/// <summary>
/// A supergroup's top-entity activity data: the channel id, a set of per-day per-user breakdown records
/// across the six top-entity metrics, the aggregation day range, and the requested per-list cap. The user
/// pool is small enough that overlaps across metrics/days are frequent (exercising the distinct-users
/// coverage) yet large enough that lists routinely exceed the 10-entry cap.
/// </summary>
public sealed record TopEntitiesFixture(
    long ChannelId,
    IReadOnlyList<TopEntityRecordFixture> Records,
    int MinDayUtc,
    int MaxDayUtc,
    int PerListMax)
{
    public override string ToString() =>
        $"TopEntities(channel={ChannelId}, records={Records.Count}, range=[{MinDayUtc},{MaxDayUtc}], cap={PerListMax})";
}

/// <summary>FsCheck arbitrary surface for Property 4's top-entity fixtures.</summary>
public static class TopEntitiesArbitraries
{
    private const int SecondsPerDay = 86_400;
    private const int BaseUtcDay = 1_690_848_000; // 2023-08-01 00:00:00 UTC.

    private static readonly string[] Metrics =
    {
        StatsMetricNames.TopPosterMessages,
        StatsMetricNames.TopPosterChars,
        StatsMetricNames.TopAdminDeleted,
        StatsMetricNames.TopAdminKicked,
        StatsMetricNames.TopAdminBanned,
        StatsMetricNames.TopInviterInvitations,
    };

    // Values map for a single (metric, day) record: 1..15 users (so lists cross the 10-entry cap),
    // drawn from a pool of 20 ids (so overlaps across metrics/days recur), each with a non-negative value.
    private static Gen<Dictionary<long, long>> BreakdownValues =>
        from userCount in Gen.Choose(1, 15)
        from userIds in StatsGen.ArrayOfLength(userCount, Gen.Choose(1, 20).Select(i => (long)i))
        from values in StatsGen.ArrayOfLength(userCount, Gen.Choose(0, 5_000).Select(i => (long)i))
        select userIds
            .Distinct()
            .Select((id, idx) => (id, value: values[idx]))
            .ToDictionary(t => t.id, t => t.value);

    private static Gen<TopEntityRecordFixture> Record =>
        from metric in Gen.Elements(Metrics)
        from dayOffset in Gen.Choose(0, 20)
        from values in BreakdownValues
        select new TopEntityRecordFixture(
            metric,
            BaseUtcDay + dayOffset * SecondsPerDay,
            values);

    private static Gen<TopEntitiesFixture> Fixture =>
        from channelId in Gen.Choose(1, 1_000_000).Select(i => (long)i + 1000)
        from count in Gen.Choose(0, 60)
        from records in StatsGen.ArrayOfLength(count, Record)
        // A cap spanning <=0, small, and the documented 10 so the bounded/selection behaviour is
        // exercised across and beyond the cap boundary.
        from perListMax in Gen.Choose(-2, 12)
        // Range endpoints drawn on and around the recorded days so both full-coverage and partial /
        // zero-fill ranges arise. When there are no records, use arbitrary aligned days (expected empty).
        from range in RangeFor(records)
        select new TopEntitiesFixture(channelId, records, range.min, range.max, perListMax);

    private static Gen<(int min, int max)> RangeFor(TopEntityRecordFixture[] records)
    {
        if (records.Length == 0)
        {
            return from a in StatsGen.AlignedUtcDay
                   from b in StatsGen.AlignedUtcDay
                   select (Math.Min(a, b), Math.Max(a, b));
        }

        var candidates = new List<int>(records.Length * 3);
        foreach (var record in records)
        {
            candidates.Add(record.UtcDay - SecondsPerDay);
            candidates.Add(record.UtcDay);
            candidates.Add(record.UtcDay + SecondsPerDay);
        }

        return from a in Gen.Elements(candidates.ToArray())
               from b in Gen.Elements(candidates.ToArray())
               select (Math.Min(a, b), Math.Max(a, b));
    }

    public static Arbitrary<TopEntitiesFixture> TopEntitiesFixture() => Arb.From(Fixture);
}
