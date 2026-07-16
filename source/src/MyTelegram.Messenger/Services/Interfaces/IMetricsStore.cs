using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Metrics_Store: records and reads per-day statistics counters, and derives the reporting
/// <c>period</c>, aggregate values, per-day series, recent-post interactions, and top-entity lists.
/// </summary>
public interface IMetricsStore
{
    /// <summary>
    /// Upserts (<c>$inc</c>) a per-day counter for <paramref name="entity"/> and <paramref name="metric"/> on
    /// the given <paramref name="utcDay"/> (Unix seconds at <c>00:00:00 UTC</c>), with an optional category breakdown.
    /// </summary>
    Task RecordAsync(StatsEntityKey entity, string metric, int utcDay, long delta, IReadOnlyDictionary<string, long>? breakdown = null);

    /// <summary>
    /// Returns the reporting <c>period</c> for the entity, or <c>{0,0}</c> when no metric was ever recorded.
    /// </summary>
    Task<StatsDateRange> GetPeriodAsync(StatsEntityKey entity, int reportingWindowDays);

    /// <summary>
    /// Returns the sum of the per-day values of <paramref name="metric"/> across
    /// <c>[minDayUtc, maxDayUtc]</c>, treating days with no metric as <c>0</c>.
    /// </summary>
    Task<long> AggregateAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc);

    /// <summary>
    /// Returns the per-day series of <paramref name="metric"/> across <c>[minDayUtc, maxDayUtc]</c>.
    /// </summary>
    Task<IReadOnlyList<DailyPoint>> GetSeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc);

    /// <summary>
    /// Returns the per-category per-day series of <paramref name="metric"/> across <c>[minDayUtc, maxDayUtc]</c>.
    /// </summary>
    Task<IReadOnlyList<CategorySeries>> GetCategorySeriesAsync(StatsEntityKey entity, string metric, int minDayUtc, int maxDayUtc);

    /// <summary>
    /// Returns the channel's recent post/story interaction counters, newest first, capped at <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<PostInteraction>> GetRecentPostInteractionsAsync(long channelId, int max = 100);

    /// <summary>
    /// Returns the top posters/admins/inviters (each capped at <paramref name="perListMax"/>, descending by
    /// activity) plus the distinct referenced user ids, over <c>[minDayUtc, maxDayUtc]</c>.
    /// </summary>
    Task<TopEntities> GetTopEntitiesAsync(long channelId, int minDayUtc, int maxDayUtc, int perListMax = 10);
}
