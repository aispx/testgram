using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Builds the dense last-<see cref="WindowDays"/>-days revenue <see cref="GraphSpec"/> for
/// <c>payments.getStarsRevenueStats</c>. Days without transactions contribute <c>0</c> so the emitted
/// <c>statsGraph</c> always spans the full window — client chart parsers require at least 2 x points
/// and crash on shorter axes.
/// </summary>
public static class RevenueGraphHelper
{
    public const int WindowDays = 30;

    private const int SecondsPerDay = 86_400;
    private const long MillisPerSecond = 1000L;

    /// <summary>
    /// The day-aligned Unix-second timestamp of the first UTC day of the window ending at
    /// <paramref name="nowUnix"/>'s day. Use as the lower bound of the transaction query so every
    /// bucketed day falls inside the dense window.
    /// </summary>
    public static int WindowStartDay(int nowUnix)
    {
        var maxDay = nowUnix - nowUnix % SecondsPerDay;
        return maxDay - (WindowDays - 1) * SecondsPerDay;
    }

    /// <summary>
    /// Builds a <see cref="WindowDays"/>-point per-day revenue spec ending at <paramref name="nowUnix"/>'s
    /// UTC day. <paramref name="totalsByUtcDay"/> keys are day-aligned Unix-second timestamps.
    /// </summary>
    public static GraphSpec BuildDailyRevenueSpec(IReadOnlyDictionary<long, long> totalsByUtcDay, int nowUnix)
    {
        var maxDay = nowUnix - nowUnix % SecondsPerDay;
        var xAxis = new List<long>(WindowDays);
        var values = new List<long>(WindowDays);
        for (var i = WindowDays - 1; i >= 0; i--)
        {
            var day = maxDay - i * SecondsPerDay;
            xAxis.Add(day * MillisPerSecond);
            values.Add(totalsByUtcDay.GetValueOrDefault(day));
        }

        var series = new[] { new GraphSeries("y0", "Revenue", "secondary", values) };
        return new GraphSpec(GraphKind.Bar, xAxis, series);
    }
}
