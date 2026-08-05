using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Regression tests for the <c>divide by zero</c> crash in the Android statistics screen.
///
/// <para>Graph type 1 (<c>iv_interactions_graph</c>, <c>interactions_graph</c>,
/// <c>story_interactions_graph</c>, and the megagroup <c>actions_graph</c>) is parsed by
/// <c>DoubleLinearChartData</c>, which scales every series against the largest one:</para>
/// <code>
/// linesK[i] = max / lines.get(i).maxValue;
/// </code>
/// <para>The client's <c>maxValue</c> starts at <c>0</c> and only grows on positive samples, so a series
/// with no positive value leaves <c>maxValue == 0</c> and the division throws
/// <c>java.lang.ArithmeticException: divide by zero</c> from <c>ChartData</c>'s constructor — which
/// happens inside <c>StatisticActivity.loadStatistic</c>'s response callback and kills the whole
/// statistics screen (observed: <c>FATAL EXCEPTION: stageQueue</c>).</para>
///
/// <para>This is reachable with perfectly ordinary data: a channel with recorded views but no shares
/// yet. The builder previously only rejected a graph whose <em>every</em> series was zero, so the
/// mixed case (views populated, shares all zero) went out as a <c>statsGraph</c> and crashed the client.
/// Stacked-bar and pie graphs do no cross-series division, so a zero category there must still be
/// served.</para>
/// </summary>
public class DoubleLinearZeroSeriesTests
{
    private static GraphBuilder CreateBuilder() => new(new FakeAsyncGraphStore());

    private static IStatsGraph BuildInline(GraphSpec spec) =>
        CreateBuilder().BuildInlineAsync(spec, dark: false, snapshotId: "snapshot", nowUnix: 0)
            .GetAwaiter().GetResult();

    private static readonly List<long> XAxis = [1_785_715_200_000, 1_785_801_600_000, 1_785_888_000_000];

    [Fact]
    public void A_line_pair_whose_second_series_is_all_zero_yields_statsGraphError()
    {
        // The real shape from this server: the channel has views on every day but no shares at all.
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("views", "Views", "primary", [11L, 6L, 2L]),
            new GraphSeries("shares", "Shares", "secondary", [0L, 0L, 0L]),
        ]);

        var error = BuildInline(spec).ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_line_pair_whose_first_series_is_all_zero_yields_statsGraphError()
    {
        // Order must not matter: linesK is computed for every series, so either being zero divides by zero.
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("iv_views", "IV views", "primary", [0L, 0L, 0L]),
            new GraphSeries("iv_shares", "IV shares", "secondary", [3L, 1L, 4L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraphError>();
    }

    [Fact]
    public void A_line_pair_with_both_series_populated_is_served_as_a_statsGraph()
    {
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("views", "Views", "primary", [11L, 6L, 2L]),
            new GraphSeries("shares", "Shares", "secondary", [1L, 0L, 1L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraph>();
    }

    [Fact]
    public void A_single_line_series_that_is_zero_on_some_days_is_still_served()
    {
        // Single-series line graphs (followers/mute/members) go through ChartData, not
        // DoubleLinearChartData, so interior zeros are fine as long as the series is not entirely zero.
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("followers", "Followers", "primary", [0L, 23L, 0L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraph>();
    }

    [Fact]
    public void A_stacked_bar_graph_with_an_empty_category_is_still_served()
    {
        // Stacked bars sum categories instead of scaling them against each other, so an empty category
        // is a legitimate zero column and must not discard the whole graph.
        var spec = new GraphSpec(GraphKind.StackedBar, XAxis,
        [
            new GraphSeries("👍", "👍", "primary", [2L, 0L, 1L]),
            new GraphSeries("🔥", "🔥", "secondary", [0L, 0L, 0L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraph>();
    }

    [Fact]
    public void A_pie_graph_with_an_empty_slice_is_still_served()
    {
        var spec = new GraphSpec(GraphKind.Pie, XAxis,
        [
            new GraphSeries("en", "English", "primary", [5L, 4L, 6L]),
            new GraphSeries("ru", "Russian", "secondary", [0L, 0L, 0L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraph>();
    }

    [Fact]
    public void An_all_zero_line_pair_still_yields_statsGraphError()
    {
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("story_views", "Story views", "primary", [0L, 0L, 0L]),
            new GraphSeries("story_shares", "Story shares", "secondary", [0L, 0L, 0L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraphError>();
    }

    [Fact]
    public void A_line_pair_whose_series_is_only_negative_yields_statsGraphError()
    {
        // maxValue only grows on positive samples, so an all-negative series also leaves it at 0.
        var spec = new GraphSpec(GraphKind.Line, XAxis,
        [
            new GraphSeries("messages", "Messages", "primary", [11L, 6L, 2L]),
            new GraphSeries("actions", "Actions", "secondary", [-1L, -4L, -2L]),
        ]);

        BuildInline(spec).ShouldBeOfType<TStatsGraphError>();
    }
}
