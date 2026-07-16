using System.Text.Json.Nodes;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 2.5 — example/edge-case unit tests for the Graph_Builder.
///
/// These complement the Graph_Builder property tests (Properties 10/11) by pinning down the two
/// boundary behaviours that the properties do not force on every run:
/// <list type="bullet">
///   <item>An empty metrics series is serialized to a <c>statsGraph</c> (with an <c>x</c> column and
///   zero data), never a <c>statsGraphError</c> (Requirements 4.4, 8.3).</item>
///   <item>A series the Graph_Builder cannot serialize yields a <c>statsGraphError</c> carrying an error
///   string, never a partial/malformed <c>statsGraph</c> (Requirement 8.6).</item>
/// </list>
/// </summary>
public class GraphBuilderEdgeCaseTests
{
    private static GraphBuilder CreateBuilder() => new(new FakeAsyncGraphStore());

    private static IStatsGraph BuildInline(GraphBuilder builder, GraphSpec spec, bool dark = false) =>
        builder.BuildInlineAsync(spec, dark, snapshotId: "snapshot", nowUnix: 0)
            .GetAwaiter().GetResult();

    // ----- Empty series -> statsGraph, not statsGraphError (Requirements 4.4, 8.3) -----

    [Fact]
    public void Empty_series_with_no_data_columns_yields_statsGraph_with_empty_x_column()
    {
        var builder = CreateBuilder();
        // No x-axis points and no data series at all: the "no recorded metric" shape used by the per-item
        // graphs (views_graph / reactions_by_emotion_graph) when a message/story has no data.
        var spec = new GraphSpec(GraphKind.Line, new List<long>(), new List<GraphSeries>());

        var graph = BuildInline(builder, spec);

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var root = JsonNode.Parse(statsGraph.Json.ShouldBeOfType<TDataJSON>().Data)
            .ShouldBeOfType<JsonObject>();

        // The document still carries the x axis column, with zero timestamp entries.
        var columns = root["columns"].ShouldBeOfType<JsonArray>();
        columns.Count.ShouldBe(1);
        var xColumn = columns[0].ShouldBeOfType<JsonArray>();
        xColumn.Count.ShouldBe(1);
        xColumn[0]!.GetValue<string>().ShouldBe("x");

        // No data-series entries in any of the maps (aside from the x axis type marker).
        root["types"].ShouldBeOfType<JsonObject>()["x"]!.GetValue<string>().ShouldBe("x");
        root["names"].ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
        root["colors"].ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
    }

    [Fact]
    public void Empty_series_with_data_column_but_no_points_yields_statsGraph_with_empty_columns()
    {
        var builder = CreateBuilder();
        // A data series exists but there are zero recorded points: the x column has zero timestamps and the
        // single data column has zero values (Requirement 8.3), still a statsGraph.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long>(),
            new List<GraphSeries> { new("y0", "Views", "primary", new List<long>()) });

        var graph = BuildInline(builder, spec);

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var root = JsonNode.Parse(statsGraph.Json.ShouldBeOfType<TDataJSON>().Data)
            .ShouldBeOfType<JsonObject>();

        var columns = root["columns"].ShouldBeOfType<JsonArray>();
        columns.Count.ShouldBe(2);

        // x column: only the "x" id, no timestamps.
        columns[0].ShouldBeOfType<JsonArray>().Count.ShouldBe(1);

        // data column: only the "y0" id, no values.
        var dataColumn = columns[1].ShouldBeOfType<JsonArray>();
        dataColumn.Count.ShouldBe(1);
        dataColumn[0]!.GetValue<string>().ShouldBe("y0");

        // The maps still describe the single data-series column.
        root["types"].ShouldBeOfType<JsonObject>().ContainsKey("y0").ShouldBeTrue();
        root["names"].ShouldBeOfType<JsonObject>()["y0"]!.GetValue<string>().ShouldBe("Views");
        root["colors"].ShouldBeOfType<JsonObject>().ContainsKey("y0").ShouldBeTrue();
    }

    [Fact]
    public void Empty_series_never_produces_a_statsGraphError()
    {
        var builder = CreateBuilder();
        var spec = new GraphSpec(GraphKind.Line, new List<long>(), new List<GraphSeries>());

        var graph = BuildInline(builder, spec);

        graph.ShouldBeOfType<TStatsGraph>();
        graph.ShouldNotBeOfType<TStatsGraphError>();
    }

    // ----- Unserializable series -> statsGraphError (Requirement 8.6) -----

    [Fact]
    public void Series_with_null_values_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // A data series whose Values collection is null cannot be serialized into a valid document.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", null!) });

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Null_series_member_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // A null entry inside the Series list is an unserializable spec.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long>(),
            new List<GraphSeries> { null! });

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Unserializable_series_never_produces_a_partial_statsGraph()
    {
        var builder = CreateBuilder();
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", null!) });

        var graph = BuildInline(builder, spec);

        graph.ShouldBeOfType<TStatsGraphError>();
        graph.ShouldNotBeOfType<TStatsGraph>();
    }
}
