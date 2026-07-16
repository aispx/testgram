namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// A single per-day statistics counter, stored in the <c>stats_metrics_daily</c> MongoDB collection.
/// <para>There is exactly one document per <c>(entity, metric, utcDay)</c> tuple; the composite
/// <see cref="Id"/> encodes that tuple so upserts are idempotent.</para>
/// </summary>
public class StatsMetricDailyDocument
{
    /// <summary>
    /// Composite id: <c>"{type}:{ownerPeerId}:{itemId}:{metric}:{utcDay}"</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Channel / Message / Story (see <see cref="StatsEntityType"/>).</summary>
    public int EntityType { get; set; }

    /// <summary>The channel id or story owner peer id.</summary>
    public long OwnerPeerId { get; set; }

    /// <summary><c>0</c> for a channel/supergroup, the message id, or the story id.</summary>
    public long ItemId { get; set; }

    /// <summary>The metric name, e.g. <c>followers</c>, <c>views</c>, <c>shares</c>, <c>reactions</c>.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>Unix seconds aligned to <c>00:00:00 UTC</c> (the day key).</summary>
    public int UtcDay { get; set; }

    /// <summary>The accumulated counter value, or the absolute gauge value for gauge-family metrics.</summary>
    public long Value { get; set; }

    /// <summary>An optional category → value breakdown (source, language, emotion, hour, weekday, user id, ...).</summary>
    public Dictionary<string, long>? Breakdown { get; set; }
}
