using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 10: Statistics graph JSON has the required structure.
///
/// For any metrics series, the produced <c>statsGraph.json</c> is a <c>dataJSON</c> whose document has a
/// <c>columns</c> array whose first column has id <c>"x"</c> with Unix-millisecond timestamps in strictly
/// ascending order, followed by one array per data series, together with <c>types</c>, <c>names</c>, and
/// <c>colors</c> maps that each contain exactly one entry keyed by the column id for every data-series
/// column.
///
/// Validates: Requirements 8.1, 8.2.
///
/// The shared <see cref="StatsGen.GraphSpec"/> generator covers empty, single-series, multi-series, and
/// zoom cases with the theme flag toggled, so this single property exercises all of those shapes. Each
/// run executes a minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class GraphJsonStructurePropertyTests
{
    [Property]
    public void Graph_json_has_the_required_structure(GraphSpecFixture fixture)
    {
        var builder = new GraphBuilder(new FakeAsyncGraphStore());
        var spec = ToProductionSpec(fixture);

        // Requirement 8.1: inline serialization yields a statsGraph whose json is a dataJSON.
        var graph = builder.BuildInlineAsync(spec, fixture.Dark, snapshotId: "snapshot", nowUnix: 0)
            .GetAwaiter().GetResult();

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var dataJson = statsGraph.Json.ShouldBeOfType<TDataJSON>();
        dataJson.Data.ShouldNotBeNullOrEmpty();

        var root = JsonNode.Parse(dataJson.Data).ShouldBeOfType<JsonObject>();

        // Requirement 8.2: a columns array is present.
        var columns = root["columns"].ShouldBeOfType<JsonArray>();

        // One x column plus exactly one column per data series.
        columns.Count.ShouldBe(fixture.Series.Count + 1);

        // First column is the x axis with Unix-millisecond timestamps in strictly ascending order.
        var xColumn = columns[0].ShouldBeOfType<JsonArray>();
        xColumn.Count.ShouldBe(fixture.XAxisMillis.Count + 1);
        xColumn[0]!.GetValue<string>().ShouldBe("x");

        var timestamps = new List<long>(xColumn.Count - 1);
        for (var i = 1; i < xColumn.Count; i++)
        {
            timestamps.Add(xColumn[i]!.GetValue<long>());
        }

        timestamps.ShouldBe(fixture.XAxisMillis.ToList());
        for (var i = 1; i < timestamps.Count; i++)
        {
            timestamps[i].ShouldBeGreaterThan(timestamps[i - 1]);
        }

        var types = root["types"].ShouldBeOfType<JsonObject>();
        var names = root["names"].ShouldBeOfType<JsonObject>();
        var colors = root["colors"].ShouldBeOfType<JsonObject>();

        // The x column is typed as the axis in the types map.
        types["x"]!.GetValue<string>().ShouldBe("x");

        // Each remaining column is a data series [id, v0, v1, ...] aligned with the x axis, and each of
        // types/names/colors carries exactly one entry keyed by that data-series column id.
        for (var c = 1; c < columns.Count; c++)
        {
            var seriesFixture = fixture.Series[c - 1];
            var column = columns[c].ShouldBeOfType<JsonArray>();

            column[0]!.GetValue<string>().ShouldBe(seriesFixture.Id);
            column.Count.ShouldBe(seriesFixture.Values.Count + 1);

            var values = new List<long>(column.Count - 1);
            for (var i = 1; i < column.Count; i++)
            {
                values.Add(column[i]!.GetValue<long>());
            }

            values.ShouldBe(seriesFixture.Values.ToList());

            types.ContainsKey(seriesFixture.Id).ShouldBeTrue();
            names.ContainsKey(seriesFixture.Id).ShouldBeTrue();
            colors.ContainsKey(seriesFixture.Id).ShouldBeTrue();
        }

        // names/colors contain exactly one entry per data-series column (no extras). types additionally
        // carries the single "x" axis entry.
        names.Count.ShouldBe(fixture.Series.Count);
        colors.Count.ShouldBe(fixture.Series.Count);
        types.Count.ShouldBe(fixture.Series.Count + 1);

        // Every data-series column id is distinct, so "exactly one entry keyed by the column id" holds.
        fixture.Series.Select(s => s.Id).Distinct().Count().ShouldBe(fixture.Series.Count);
    }

    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        // Map the optional zoom fixture too so zoom cases flow through BuildInlineAsync (which issues a
        // zoom token via the store); Property 10 asserts only on the produced JSON document structure.
        var zoom = fixture.Zoom is null ? null : ToProductionSpec(fixture.Zoom);

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series, zoom);
    }
}
