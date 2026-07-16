namespace MyTelegram.Messenger.Services.Stats.Ingestion;

/// <summary>
/// Helpers shared by the stats metrics-ingestion subscribers for aligning an event timestamp to the
/// UTC calendar day it occurred in (Requirement 10.1).
/// </summary>
public static class StatsIngestionTime
{
    private const int SecondsPerDay = 86400;

    /// <summary>
    /// Aligns a Unix-second timestamp to <c>00:00:00 UTC</c> of the calendar day it falls in — the day key
    /// used by the Metrics_Store (the day bounded by <c>00:00:00 UTC</c> inclusive and the next
    /// <c>00:00:00 UTC</c> exclusive).
    /// </summary>
    public static int ToUtcDay(long unixSeconds)
    {
        var day = unixSeconds - Modulo(unixSeconds, SecondsPerDay);
        return (int)day;
    }

    /// <summary>
    /// Returns <paramref name="candidateUnixSeconds"/> aligned to the UTC day when it is a positive
    /// timestamp, otherwise the current UTC day. Used when an event carries its own occurrence date but
    /// may leave it unset.
    /// </summary>
    public static int ToUtcDayOrNow(int candidateUnixSeconds)
    {
        var seconds = candidateUnixSeconds > 0 ? candidateUnixSeconds : DateTime.UtcNow.ToTimestamp();
        return ToUtcDay(seconds);
    }

    /// <summary>Returns the current UTC day (<c>00:00:00 UTC</c> Unix seconds).</summary>
    public static int CurrentUtcDay() => ToUtcDay(DateTime.UtcNow.ToTimestamp());

    // Non-negative modulo so timestamps before the epoch still bucket to the correct day boundary.
    private static long Modulo(long value, long modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
