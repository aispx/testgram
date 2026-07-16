namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The kind of entity a statistics metric or series belongs to.
/// </summary>
public enum StatsEntityType
{
    Channel,
    Message,
    Story
}

/// <summary>
/// Identifies the entity a metric is recorded against.
/// <para><c>ItemId</c> is <c>0</c> for a channel/supergroup, the message id for a message,
/// or the story id for a story.</para>
/// </summary>
public readonly record struct StatsEntityKey(StatsEntityType Type, long OwnerPeerId, long ItemId);

/// <summary>
/// A statistics reporting period expressed as <c>statsDateRangeDays{min_date, max_date}</c>.
/// <para>Both values are Unix-second timestamps aligned to <c>00:00:00 UTC</c>.</para>
/// </summary>
public readonly record struct StatsDateRange(int MinDate, int MaxDate);

/// <summary>
/// A single per-day metric data point.
/// <para><c>UtcDay</c> is a Unix-second timestamp aligned to <c>00:00:00 UTC</c> (the day key).</para>
/// </summary>
public readonly record struct DailyPoint(int UtcDay, long Value);

/// <summary>
/// A named category (source, language, emotion, hour, weekday, ...) with its per-day series.
/// </summary>
public sealed record CategorySeries(string Category, IReadOnlyList<DailyPoint> Points);
