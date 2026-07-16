using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// MongoDB-backed implementation of <see cref="IMetricsStore"/> over the <c>stats_metrics_daily</c>
/// collection. Records per-day statistics counters and derives the reporting <c>period</c>, aggregate
/// values, per-day series, recent-post interactions, and top-entity lists.
/// </summary>
public class MetricsStore : IMetricsStore, ISingletonDependency
{
    private const string CollectionName = "stats_metrics_daily";
    private const int SecondsPerDay = 86400;
    private const int MinReportingWindowDays = 1;
    private const int MaxReportingWindowDays = 365;

    private readonly IMongoDatabase _mongoDatabase;
    private readonly Lazy<Task> _indexInit;

    public MetricsStore(IMongoDatabase mongoDatabase)
    {
        _mongoDatabase = mongoDatabase;
        _indexInit = new Lazy<Task>(CreateIndexesAsync);
    }

    private IMongoCollection<StatsMetricDailyDocument> Collection =>
        _mongoDatabase.GetCollection<StatsMetricDailyDocument>(CollectionName);

    public async Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta,
        IReadOnlyDictionary<string, long>? breakdown = null)
    {
        await EnsureIndexesAsync();

        var id = BuildId(entity, metric, utcDay);
        var builder = Builders<StatsMetricDailyDocument>.Update;
        var isGauge = StatsMetricNames.IsGauge(metric);

        var updates = new List<UpdateDefinition<StatsMetricDailyDocument>>
        {
            builder.SetOnInsert(d => d.EntityType, (int)entity.Type),
            builder.SetOnInsert(d => d.OwnerPeerId, entity.OwnerPeerId),
            builder.SetOnInsert(d => d.ItemId, entity.ItemId),
            builder.SetOnInsert(d => d.Metric, metric),
            builder.SetOnInsert(d => d.UtcDay, utcDay),
            isGauge ? builder.Set(d => d.Value, delta) : builder.Inc(d => d.Value, delta)
        };

        if (breakdown is { Count: > 0 })
        {
            foreach (var (category, value) in breakdown)
            {
                var field = $"Breakdown.{category}";
                updates.Add(isGauge ? builder.Set(field, value) : builder.Inc(field, value));
            }
        }

        await Collection.UpdateOneAsync(
            d => d.Id == id,
            builder.Combine(updates),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays)
    {
        await EnsureIndexesAsync();

        var window = Math.Clamp(reportingWindowDays, MinReportingWindowDays, MaxReportingWindowDays);

        var mostRecent = await Collection
            .Find(BuildEntityFilter(entity))
            .SortByDescending(d => d.UtcDay)
            .Limit(1)
            .FirstOrDefaultAsync();

        if (mostRecent == null)
        {
            return new StatsDateRange(0, 0);
        }

        var maxDate = mostRecent.UtcDay;
        var minDate = maxDate - window * SecondsPerDay;
        return new StatsDateRange(minDate, maxDate);
    }

    public async Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        await EnsureIndexesAsync();

        if (maxDayUtc < minDayUtc)
        {
            return 0;
        }

        var docs = await Collection.Find(BuildMetricRangeFilter(entity, metric, minDayUtc, maxDayUtc)).ToListAsync();
        return docs.Sum(d => d.Value);
    }

    public async Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        await EnsureIndexesAsync();

        if (maxDayUtc < minDayUtc)
        {
            return [];
        }

        var docs = await Collection
            .Find(BuildMetricRangeFilter(entity, metric, minDayUtc, maxDayUtc))
            .SortBy(d => d.UtcDay)
            .ToListAsync();

        return docs.Select(d => new DailyPoint(d.UtcDay, d.Value)).ToList();
    }

    public async Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        await EnsureIndexesAsync();

        if (maxDayUtc < minDayUtc)
        {
            return [];
        }

        var docs = await Collection
            .Find(BuildMetricRangeFilter(entity, metric, minDayUtc, maxDayUtc))
            .SortBy(d => d.UtcDay)
            .ToListAsync();

        var byCategory = new Dictionary<string, List<DailyPoint>>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            if (doc.Breakdown == null)
            {
                continue;
            }

            foreach (var (category, value) in doc.Breakdown)
            {
                if (!byCategory.TryGetValue(category, out var points))
                {
                    points = [];
                    byCategory[category] = points;
                }

                points.Add(new DailyPoint(doc.UtcDay, value));
            }
        }

        return byCategory
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CategorySeries(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100)
    {
        await EnsureIndexesAsync();

        var cap = max <= 0 ? 0 : max;
        if (cap == 0)
        {
            return [];
        }

        var filter = Builders<StatsMetricDailyDocument>.Filter.And(
            Builders<StatsMetricDailyDocument>.Filter.Eq(d => d.OwnerPeerId, channelId),
            Builders<StatsMetricDailyDocument>.Filter.In(d => d.EntityType,
                new[] { (int)StatsEntityType.Message, (int)StatsEntityType.Story }));

        var docs = await Collection.Find(filter).ToListAsync();

        var interactions = docs
            .GroupBy(d => (d.EntityType, d.ItemId))
            .Select(g =>
            {
                var views = g.Where(d => d.Metric == StatsMetricNames.Views).Sum(d => d.Value);
                var forwards = g.Where(d => d.Metric == StatsMetricNames.Shares).Sum(d => d.Value);
                var reactions = g.Where(d => d.Metric == StatsMetricNames.Reactions).Sum(d => d.Value);

                var dateMetric = g.Where(d => d.Metric == StatsMetricNames.PostDate).ToList();
                var date = dateMetric.Count > 0
                    ? dateMetric.Max(d => d.Value)
                    : g.Max(d => (long)d.UtcDay);

                return new PostInteraction(
                    (StatsEntityType)g.Key.EntityType,
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

        return interactions;
    }

    public async Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10)
    {
        await EnsureIndexesAsync();

        var channel = new StatsEntityKey(StatsEntityType.Channel, channelId, 0);
        var cap = perListMax <= 0 ? 0 : perListMax;

        var posterMessages = await AggregateBreakdownAsync(channel, StatsMetricNames.TopPosterMessages, minDayUtc, maxDayUtc);
        var posterChars = await AggregateBreakdownAsync(channel, StatsMetricNames.TopPosterChars, minDayUtc, maxDayUtc);
        var adminDeleted = await AggregateBreakdownAsync(channel, StatsMetricNames.TopAdminDeleted, minDayUtc, maxDayUtc);
        var adminKicked = await AggregateBreakdownAsync(channel, StatsMetricNames.TopAdminKicked, minDayUtc, maxDayUtc);
        var adminBanned = await AggregateBreakdownAsync(channel, StatsMetricNames.TopAdminBanned, minDayUtc, maxDayUtc);
        var inviterInvitations = await AggregateBreakdownAsync(channel, StatsMetricNames.TopInviterInvitations, minDayUtc, maxDayUtc);

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

        var adminUserIds = adminDeleted.Keys
            .Union(adminKicked.Keys)
            .Union(adminBanned.Keys);
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

        return new TopEntities(posters, admins, inviters, userIds);
    }

    private async Task<Dictionary<long, long>> AggregateBreakdownAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        if (maxDayUtc < minDayUtc)
        {
            return new Dictionary<long, long>();
        }

        var docs = await Collection.Find(BuildMetricRangeFilter(entity, metric, minDayUtc, maxDayUtc)).ToListAsync();

        var totals = new Dictionary<long, long>();
        foreach (var doc in docs)
        {
            if (doc.Breakdown == null)
            {
                continue;
            }

            foreach (var (category, value) in doc.Breakdown)
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

    private static string BuildId(StatsEntityKey entity, string metric, int utcDay) =>
        $"{(int)entity.Type}:{entity.OwnerPeerId}:{entity.ItemId}:{metric}:{utcDay}";

    private static FilterDefinition<StatsMetricDailyDocument> BuildEntityFilter(StatsEntityKey entity)
    {
        var f = Builders<StatsMetricDailyDocument>.Filter;
        return f.And(
            f.Eq(d => d.EntityType, (int)entity.Type),
            f.Eq(d => d.OwnerPeerId, entity.OwnerPeerId),
            f.Eq(d => d.ItemId, entity.ItemId));
    }

    private static FilterDefinition<StatsMetricDailyDocument> BuildMetricRangeFilter(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc)
    {
        var f = Builders<StatsMetricDailyDocument>.Filter;
        return f.And(
            BuildEntityFilter(entity),
            f.Eq(d => d.Metric, metric),
            f.Gte(d => d.UtcDay, minDayUtc),
            f.Lte(d => d.UtcDay, maxDayUtc));
    }

    private Task EnsureIndexesAsync() => _indexInit.Value;

    private async Task CreateIndexesAsync()
    {
        var keys = Builders<StatsMetricDailyDocument>.IndexKeys;

        var uniqueIndex = new CreateIndexModel<StatsMetricDailyDocument>(
            keys.Ascending(d => d.EntityType)
                .Ascending(d => d.OwnerPeerId)
                .Ascending(d => d.ItemId)
                .Ascending(d => d.Metric)
                .Ascending(d => d.UtcDay),
            new CreateIndexOptions { Unique = true, Name = "stats_metrics_daily_entity_metric_day" });

        var scanIndex = new CreateIndexModel<StatsMetricDailyDocument>(
            keys.Ascending(d => d.EntityType)
                .Ascending(d => d.OwnerPeerId)
                .Ascending(d => d.ItemId)
                .Ascending(d => d.UtcDay),
            new CreateIndexOptions { Name = "stats_metrics_daily_entity_day" });

        await Collection.Indexes.CreateManyAsync(new[] { uniqueIndex, scanIndex });
    }
}
