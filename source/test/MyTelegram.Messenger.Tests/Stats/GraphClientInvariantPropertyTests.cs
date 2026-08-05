using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 21: every emitted <c>statsGraph</c> satisfies the client chart-parser
/// invariants.
///
/// Client chart parsers (DrKLO <c>ChartData.measure</c>, <c>StackBarChartData</c>,
/// <c>StackLinearChartData</c>) construct the chart before validating it and crash with
/// <c>ArrayIndexOutOfBoundsException</c>/<c>IndexOutOfBoundsException</c> on an x axis with fewer than 2
/// points, zero data columns, or columns misaligned with the x axis. For any graph spec,
/// <c>BuildInlineAsync</c> therefore returns a <c>statsGraph</c> iff the spec has at least 2 x points
/// (the shared generator always aligns series values with the x axis), and every emitted
/// <c>statsGraph</c> JSON satisfies: x length ≥ 2; every column length equals x length + 1 (id plus one
/// value per x point); at least one data column; <c>types</c>/<c>names</c>/<c>colors</c> each carry an
/// entry for every data column; <c>types["x"] == "x"</c>; every color is a <c>#RRGGBB</c> hex value.
/// Degenerate specs yield a <c>statsGraphError</c> with a non-empty message instead.
///
/// Each run executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class GraphClientInvariantPropertyTests
{
    /// <summary>
    /// The wire form real Telegram uses for chart colors: an optional theme name followed by the hex value
    /// (<c>BLUE#007AFF</c>). The Android client parses it with <c>Pattern.compile("(.*)(#.*)")</c>, taking
    /// the prefix as the theme key <c>statisticChartLine_&lt;name&gt;</c> and the remainder as the literal
    /// color, so a bare <c>#RRGGBB</c> is also valid.
    /// </summary>
    private static readonly Regex HexColor = new("^[A-Za-z]*#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    [Property]
    public void Emitted_statsGraph_always_satisfies_client_chart_invariants(GraphSpecFixture fixture)
    {
        var builder = new GraphBuilder(new FakeAsyncGraphStore());
        var spec = ToProductionSpec(fixture);

        var graph = builder.BuildInlineAsync(spec, fixture.Dark, snapshotId: "snapshot", nowUnix: 0)
            .GetAwaiter().GetResult();

        // The generator always aligns series values with the x axis and emits >= 1 series, so the sole
        // degeneracy dimension is the x-point count: statsGraph iff x.length >= 2.
        if (fixture.XAxisMillis.Count < 2)
        {
            var error = graph.ShouldBeOfType<TStatsGraphError>();
            error.Error.ShouldNotBeNullOrEmpty();
            return;
        }

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var root = JsonNode.Parse(statsGraph.Json.ShouldBeOfType<TDataJSON>().Data)
            .ShouldBeOfType<JsonObject>();

        var columns = root["columns"].ShouldBeOfType<JsonArray>();

        // At least one data column beyond the x axis.
        columns.Count.ShouldBeGreaterThanOrEqualTo(2);

        // x column: id plus at least 2 points.
        var xColumn = columns[0].ShouldBeOfType<JsonArray>();
        xColumn[0]!.GetValue<string>().ShouldBe("x");
        xColumn.Count.ShouldBeGreaterThanOrEqualTo(3);

        var types = root["types"].ShouldBeOfType<JsonObject>();
        var names = root["names"].ShouldBeOfType<JsonObject>();
        var colors = root["colors"].ShouldBeOfType<JsonObject>();

        types["x"]!.GetValue<string>().ShouldBe("x");

        // Every data column is aligned with the x axis and described in all three maps, with a full
        // #RRGGBB color — clients index colors.getString(id) unconditionally.
        for (var c = 1; c < columns.Count; c++)
        {
            var column = columns[c].ShouldBeOfType<JsonArray>();
            column.Count.ShouldBe(xColumn.Count);

            var id = column[0]!.GetValue<string>();
            types.ContainsKey(id).ShouldBeTrue();
            names.ContainsKey(id).ShouldBeTrue();
            colors.ContainsKey(id).ShouldBeTrue();
            HexColor.IsMatch(colors[id]!.GetValue<string>()).ShouldBeTrue(
                $"color '{colors[id]}' for series '{id}' must be a #RRGGBB hex value");
        }
    }

    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        var zoom = fixture.Zoom is null ? null : ToProductionSpec(fixture.Zoom);

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series, zoom);
    }
}
