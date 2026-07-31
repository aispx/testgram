using System.Text.Json.Nodes;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Unit tests for <see cref="RevenueGraphHelper"/>, which builds the dense 30-day revenue graph spec for
/// <c>payments.getStarsRevenueStats</c>. The spec must always span the full window (days without
/// transactions contribute 0) so the serialized <c>statsGraph</c> never degenerates into a payload that
/// crashes client chart parsers (x.length &lt; 2 or a missing <c>colors</c> entry).
/// </summary>
public class RevenueGraphSpecTests
{
    // 2023-08-01 14:26:40 UTC — deliberately mid-day to prove day alignment.
    private const int NowUnix = 1_690_900_000;
    private const int SecondsPerDay = 86_400;
    private const long MillisPerSecond = 1000L;

    private static long MaxDay => NowUnix - NowUnix % SecondsPerDay;

    [Fact]
    public void Empty_totals_yield_the_full_window_of_zeros()
    {
        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(new Dictionary<long, long>(), NowUnix);

        spec.XAxisMillis.Count.ShouldBe(RevenueGraphHelper.WindowDays);
        spec.Series.Count.ShouldBe(1);
        spec.Series[0].Values.Count.ShouldBe(RevenueGraphHelper.WindowDays);
        spec.Series[0].Values.ShouldAllBe(v => v == 0L);
    }

    [Fact]
    public void X_axis_is_day_aligned_ascending_and_ends_at_the_current_utc_day()
    {
        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(new Dictionary<long, long>(), NowUnix);

        spec.XAxisMillis[^1].ShouldBe(MaxDay * MillisPerSecond);
        for (var i = 1; i < spec.XAxisMillis.Count; i++)
        {
            (spec.XAxisMillis[i] - spec.XAxisMillis[i - 1]).ShouldBe(SecondsPerDay * MillisPerSecond);
        }
    }

    [Fact]
    public void Single_day_totals_land_on_their_day_and_other_days_stay_zero()
    {
        var day = MaxDay - 3 * SecondsPerDay;
        var totals = new Dictionary<long, long> { [day] = 1234 };

        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(totals, NowUnix);

        var dayIndex = spec.XAxisMillis.ToList().IndexOf(day * MillisPerSecond);
        dayIndex.ShouldBeGreaterThanOrEqualTo(0);
        spec.Series[0].Values[dayIndex].ShouldBe(1234);
        spec.Series[0].Values.Sum().ShouldBe(1234);
    }

    [Fact]
    public void Window_start_day_is_included_and_older_days_are_excluded()
    {
        var start = RevenueGraphHelper.WindowStartDay(NowUnix);
        var totals = new Dictionary<long, long>
        {
            [start] = 42,
            [start - SecondsPerDay] = 99
        };

        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(totals, NowUnix);

        spec.XAxisMillis[0].ShouldBe((long)start * MillisPerSecond);
        spec.Series[0].Values[0].ShouldBe(42);
        // A total outside the window never leaks into the graph.
        spec.Series[0].Values.Sum().ShouldBe(42);
    }

    [Fact]
    public void Serialized_revenue_graph_with_no_revenue_is_a_statsGraphError()
    {
        // A window without a single transaction carries no information: the client renders explanatory
        // text instead of an empty chart card.
        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(new Dictionary<long, long>(), NowUnix);

        var graph = new GraphBuilder(new FakeAsyncGraphStore())
            .BuildInlineAsync(spec, dark: false, snapshotId: "stars-revenue:test", nowUnix: NowUnix)
            .GetAwaiter().GetResult();

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Serialized_revenue_graph_is_a_statsGraph_with_a_color_for_its_series()
    {
        // One day with revenue inside the window: the graph spans the full window and renders.
        var totals = new Dictionary<long, long> { [MaxDay - 2 * SecondsPerDay] = 5_000 };
        var spec = RevenueGraphHelper.BuildDailyRevenueSpec(totals, NowUnix);

        var graph = new GraphBuilder(new FakeAsyncGraphStore())
            .BuildInlineAsync(spec, dark: false, snapshotId: "stars-revenue:test", nowUnix: NowUnix)
            .GetAwaiter().GetResult();

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var root = JsonNode.Parse(statsGraph.Json.ShouldBeOfType<TDataJSON>().Data)
            .ShouldBeOfType<JsonObject>();

        var columns = root["columns"].ShouldBeOfType<JsonArray>();
        columns.Count.ShouldBe(2);
        columns[0].ShouldBeOfType<JsonArray>().Count.ShouldBe(RevenueGraphHelper.WindowDays + 1);

        root["types"].ShouldBeOfType<JsonObject>()["y0"]!.GetValue<string>().ShouldBe("bar");
        root["names"].ShouldBeOfType<JsonObject>()["y0"]!.GetValue<string>().ShouldBe("Revenue");
        root["colors"].ShouldBeOfType<JsonObject>().ContainsKey("y0").ShouldBeTrue();
    }
}
