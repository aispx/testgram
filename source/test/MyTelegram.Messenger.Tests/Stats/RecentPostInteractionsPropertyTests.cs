using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 2: Recent-post interactions are newest-first and capped.
///
/// For any set of channel posts and stories, <c>recent_posts_interactions</c> contains at most 100
/// entries and is ordered by post/story date from most recent to least recent (dates non-increasing).
///
/// Validates: Requirements 2.3.
///
/// Storage property tests run against an in-memory store (no real MongoDB in the property loop). The
/// production <see cref="MetricsStore"/> is MongoDB-backed, so
/// <see cref="RecentPostsInMemoryMetricsStore"/> below faithfully reproduces the documented semantics of
/// <see cref="IMetricsStore.GetRecentPostInteractionsAsync"/>: group the channel's message/story records
/// by <c>(entity type, item id)</c>, sum views/shares/reactions, take the post date from the
/// <c>post_date</c> gauge (falling back to the most recent recorded day), order newest-first, and cap at
/// the requested maximum (default 100). Each run executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(RecentPostArbitraries) }, MaxTest = 100)]
public class RecentPostInteractionsPropertyTests
{
    [Property]
    public void Recent_post_interactions_are_newest_first_and_capped(RecentPostsFixture fixture)
    {
        var store = new RecentPostsInMemoryMetricsStore();
        foreach (var post in fixture.Posts)
        {
            SeedPost(store, fixture.ChannelId, post);
        }

        // The distinct post/story groups the channel actually has recorded metrics for. Each generated
        // post records at least its post_date, so every generated post forms exactly one group.
        var distinctGroups = fixture.Posts
            .Select(p => (p.IsStory, p.ItemId))
            .Distinct()
            .Count();

        // --- The default cap of 100 (the value the design property speaks about). ---
        var recent = store.GetRecentPostInteractionsAsync(fixture.ChannelId).GetAwaiter().GetResult();

        // Capped at 100 entries.
        recent.Count.ShouldBeLessThanOrEqualTo(100);
        // Exactly the available groups up to the cap (nothing dropped below the cap, nothing invented).
        recent.Count.ShouldBe(Math.Min(100, distinctGroups));
        // Ordered by date from most recent to least recent (dates non-increasing).
        AssertDatesNonIncreasing(recent);
        // Only this channel's posts/stories appear.
        foreach (var interaction in recent)
        {
            (interaction.Type is StatsEntityType.Message or StatsEntityType.Story).ShouldBeTrue();
        }

        // --- A generated cap exercises the cap boundary generally (including <= 0 and > 100). ---
        var max = fixture.RequestedMax;
        var capped = store.GetRecentPostInteractionsAsync(fixture.ChannelId, max).GetAwaiter().GetResult();

        if (max <= 0)
        {
            capped.ShouldBeEmpty();
        }
        else
        {
            capped.Count.ShouldBeLessThanOrEqualTo(max);
            capped.Count.ShouldBe(Math.Min(max, distinctGroups));
            AssertDatesNonIncreasing(capped);
        }
    }

    private static void AssertDatesNonIncreasing(IReadOnlyList<PostInteraction> interactions)
    {
        for (var i = 1; i < interactions.Count; i++)
        {
            interactions[i - 1].Date.ShouldBeGreaterThanOrEqualTo(interactions[i].Date);
        }
    }

    private static void SeedPost(RecentPostsInMemoryMetricsStore store, long channelId, RecentPostFixture post)
    {
        var type = post.IsStory ? StatsEntityType.Story : StatsEntityType.Message;
        var entity = new StatsEntityKey(type, channelId, post.ItemId);

        // Every post records its date (a post_date gauge) so it forms a group and has a well-defined order.
        store.RecordAsync(entity, StatsMetricNames.PostDate, post.DayRecords[0].UtcDay, post.Date)
            .GetAwaiter().GetResult();

        foreach (var day in post.DayRecords)
        {
            store.RecordAsync(entity, StatsMetricNames.Views, day.UtcDay, day.Views).GetAwaiter().GetResult();
            store.RecordAsync(entity, StatsMetricNames.Shares, day.UtcDay, day.Shares).GetAwaiter().GetResult();
            store.RecordAsync(entity, StatsMetricNames.Reactions, day.UtcDay, day.Reactions).GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// In-memory <see cref="IMetricsStore"/> faithful to the documented recent-post-interactions semantics of
/// the MongoDB-backed <see cref="MetricsStore"/>. Only <see cref="RecordAsync"/> and
/// <see cref="GetRecentPostInteractionsAsync"/> are needed for Property 2; the remaining members are not
/// exercised by this test.
/// </summary>
internal sealed class RecentPostsInMemoryMetricsStore : IMetricsStore
{
    private readonly Dictionary<string, MetricCell> _cells = new(StringComparer.Ordinal);

    private sealed record MetricCell(StatsEntityKey Entity, string Metric, int UtcDay)
    {
        public long Value { get; set; }
    }

    public Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
        IReadOnlyDictionary<string, long>? breakdown = null)
    {
        var id = $"{(int)entity.Type}:{entity.OwnerPeerId}:{entity.ItemId}:{metric}:{utcDay}";
        if (!_cells.TryGetValue(id, out var cell))
        {
            cell = new MetricCell(entity, metric, utcDay);
            _cells[id] = cell;
        }

        // Gauge metrics use set-semantics; counters accumulate (matches production RecordAsync).
        cell.Value = StatsMetricNames.IsGauge(metric) ? delta : cell.Value + delta;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100)
    {
        var cap = max <= 0 ? 0 : max;
        if (cap == 0)
        {
            return Task.FromResult<IReadOnlyList<PostInteraction>>(Array.Empty<PostInteraction>());
        }

        var relevant = _cells.Values
            .Where(c => c.Entity.OwnerPeerId == channelId &&
                        c.Entity.Type is StatsEntityType.Message or StatsEntityType.Story);

        var interactions = relevant
            .GroupBy(c => (c.Entity.Type, c.Entity.ItemId))
            .Select(g =>
            {
                var views = g.Where(c => c.Metric == StatsMetricNames.Views).Sum(c => c.Value);
                var forwards = g.Where(c => c.Metric == StatsMetricNames.Shares).Sum(c => c.Value);
                var reactions = g.Where(c => c.Metric == StatsMetricNames.Reactions).Sum(c => c.Value);

                var dateMetric = g.Where(c => c.Metric == StatsMetricNames.PostDate).ToList();
                var date = dateMetric.Count > 0
                    ? dateMetric.Max(c => c.Value)
                    : g.Max(c => (long)c.UtcDay);

                return new PostInteraction(
                    g.Key.Type,
                    (int)g.Key.ItemId,
                    (int)date,
                    (int)views,
                    (int)forwards,
                    (int)reactions);
            })
            .OrderByDescending(p => p.Date)
            .ThenByDescending(p => p.ItemId)
            .Take(cap)
            .ToList();

        return Task.FromResult<IReadOnlyList<PostInteraction>>(interactions);
    }

    public Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays) =>
        throw new NotSupportedException();

    public Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, long>> GetBreakdownTotalsAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc) =>
        throw new NotSupportedException();

    public Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10) =>
        throw new NotSupportedException();
}

/// <summary>One recorded per-day interaction row for a generated post/story.</summary>
public readonly record struct RecentPostDayFixture(int UtcDay, long Views, long Shares, long Reactions);

/// <summary>
/// A single generated post or story for a channel: its kind, id, post date (Unix seconds used to order
/// newest-first), and the per-day interaction rows recorded against it.
/// </summary>
public sealed record RecentPostFixture(
    bool IsStory,
    long ItemId,
    int Date,
    IReadOnlyList<RecentPostDayFixture> DayRecords)
{
    public override string ToString() =>
        $"{(IsStory ? "Story" : "Post")}(id={ItemId}, date={Date}, days={DayRecords.Count})";
}

/// <summary>
/// A set of a channel's posts and stories plus the page size requested from
/// <see cref="IMetricsStore.GetRecentPostInteractionsAsync"/>. The post count spans 0..150 so the 100-entry
/// cap boundary is crossed, and <see cref="RequestedMax"/> spans -5..150 to cover the <c>&lt;= 0</c>, small,
/// and <c>&gt; 100</c> cap cases. Item ids are unique so each post forms exactly one interaction group.
/// </summary>
public sealed record RecentPostsFixture(
    long ChannelId,
    IReadOnlyList<RecentPostFixture> Posts,
    int RequestedMax)
{
    public override string ToString() =>
        $"RecentPosts(channel={ChannelId}, posts={Posts.Count}, requestedMax={RequestedMax})";
}

/// <summary>FsCheck arbitrary surface for Property 2's recent-post fixtures.</summary>
public static class RecentPostArbitraries
{
    private const int SecondsPerDay = 86_400;
    private const int BaseUtcDay = 1_690_848_000; // 2023-08-01 00:00:00 UTC.

    /// <summary>
    /// Generates a post/story body (kind, date, per-day rows) without an id; the caller assigns a unique
    /// id by index so each generated post forms exactly one interaction group.
    /// </summary>
    private static Gen<RecentPostFixture> PostBody =>
        from isStory in Arb.Generate<bool>()
        // Post dates drawn from a small window so ties (equal dates) occur and the non-increasing
        // ordering must still hold; kept aligned to whole days but that is not required by the property.
        from dayOffset in Gen.Choose(0, 30)
        from dayCount in Gen.Choose(1, 4)
        from days in StatsGen.ArrayOfLength(dayCount, Gen.Choose(0, 60))
        from views in StatsGen.ArrayOfLength(dayCount, Gen.Choose(0, 1_000).Select(i => (long)i))
        from shares in StatsGen.ArrayOfLength(dayCount, Gen.Choose(0, 1_000).Select(i => (long)i))
        from reactions in StatsGen.ArrayOfLength(dayCount, Gen.Choose(0, 1_000).Select(i => (long)i))
        let date = BaseUtcDay + dayOffset * SecondsPerDay
        let rows = days
            .Distinct()
            .OrderBy(d => d)
            .Select((d, idx) => new RecentPostDayFixture(
                BaseUtcDay + d * SecondsPerDay,
                views[idx],
                shares[idx],
                reactions[idx]))
            .ToList()
        // ItemId is a placeholder here; RecentPosts assigns a unique id per index below.
        select new RecentPostFixture(isStory, 0, date, rows);

    private static Gen<RecentPostsFixture> RecentPosts =>
        from channelId in Gen.Choose(1, 1_000_000).Select(i => (long)i + 1000)
        // 0..150 posts so the 100-entry cap is genuinely crossed in a meaningful fraction of cases.
        from count in Gen.Choose(0, 150)
        from bodies in StatsGen.ArrayOfLength(count, PostBody)
        from requestedMax in Gen.Choose(-5, 150)
        select new RecentPostsFixture(
            channelId,
            bodies.Select((body, index) => body with { ItemId = index }).ToList(),
            requestedMax);

    public static Arbitrary<RecentPostsFixture> Fixture() => Arb.From(RecentPosts);
}
