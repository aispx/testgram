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
///   <item>A degenerate spec — fewer than 2 x points, no data series, or a series misaligned with the
///   x axis — yields a <c>statsGraphError</c>, never a <c>statsGraph</c>: client chart parsers construct
///   the chart before validating it and crash on such payloads (Requirements 4.4, 8.3).</item>
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

    // ----- Degenerate spec -> statsGraphError, never a statsGraph (Requirements 4.4, 8.3) -----

    [Fact]
    public void Empty_spec_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // No x-axis points and no data series at all: the "no recorded metric" shape used by the per-item
        // graphs (views_graph / reactions_by_emotion_graph) when a message/story has no data.
        var spec = new GraphSpec(GraphKind.Line, new List<long>(), new List<GraphSeries>());

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Series_with_no_points_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // A data series exists but there are zero recorded points: DrKLO ChartData.measure() reads x[0]
        // unconditionally, so this payload must never reach a client as a statsGraph.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long>(),
            new List<GraphSeries> { new("y0", "Views", "primary", new List<long>()) });

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Single_x_point_spec_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // One x point is still degenerate: clients require x.length >= 2 to render any chart.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", new List<long> { 5 }) });

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Series_misaligned_with_x_axis_yields_statsGraphError()
    {
        var builder = CreateBuilder();
        // A data column whose length differs from the x axis produces misaligned columns.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000, 1_690_934_400_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", new List<long> { 5 }) });

        var graph = BuildInline(builder, spec);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Degenerate_spec_never_produces_a_statsGraph()
    {
        var builder = CreateBuilder();
        var spec = new GraphSpec(GraphKind.Line, new List<long>(), new List<GraphSeries>());

        var graph = BuildInline(builder, spec);

        graph.ShouldBeOfType<TStatsGraphError>();
        graph.ShouldNotBeOfType<TStatsGraph>();
    }

    [Fact]
    public void Two_point_single_series_spec_yields_statsGraph_with_color_per_series()
    {
        var builder = CreateBuilder();
        // The minimal renderable graph: 2 x points and one aligned data column.
        var spec = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000, 1_690_934_400_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", new List<long> { 5, 7 }) });

        var graph = BuildInline(builder, spec);

        var statsGraph = graph.ShouldBeOfType<TStatsGraph>();
        var root = JsonNode.Parse(statsGraph.Json.ShouldBeOfType<TDataJSON>().Data)
            .ShouldBeOfType<JsonObject>();

        var columns = root["columns"].ShouldBeOfType<JsonArray>();
        columns.Count.ShouldBe(2);
        columns[0].ShouldBeOfType<JsonArray>().Count.ShouldBe(3);
        columns[1].ShouldBeOfType<JsonArray>().Count.ShouldBe(3);

        root["types"].ShouldBeOfType<JsonObject>()["x"]!.GetValue<string>().ShouldBe("x");
        root["names"].ShouldBeOfType<JsonObject>()["y0"]!.GetValue<string>().ShouldBe("Views");
        root["colors"].ShouldBeOfType<JsonObject>().ContainsKey("y0").ShouldBeTrue();
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
