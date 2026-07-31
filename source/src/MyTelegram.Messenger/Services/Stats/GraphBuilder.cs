using System.Text.Json;
using System.Text.Json.Nodes;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The Graph_Builder. Serializes a <see cref="GraphSpec"/> into the Telegram Statistics_Graph_JSON wire
/// format and produces <c>statsGraph</c>/<c>statsGraphError</c> objects. Colors are resolved from a light
/// or dark palette keyed by <see cref="GraphSeries.ColorKey"/>.
/// </summary>
/// <remarks>
/// A graph whose every series is entirely zero is likewise emitted as a <c>statsGraphError</c>: it would
/// render as an empty card, and <c>DoubleLinearChartData</c> scales one series against the other's maximum
/// (a zero maximum yields an infinite factor).
///
/// Wire format (verified against the reference clients — DrKLO <c>ChartData</c>, telegram-tt/tweb chart
/// parsers, tdlib):
/// <code>
/// {
///   "columns": [ ["x", t0, t1, ...], ["y0", v0, v1, ...], ... ],
///   "types":   { "x": "x", "y0": "line", ... },
///   "names":   { "y0": "Series 0", ... },
///   "colors":  { "y0": "#RRGGBB", ... }
/// }
/// </code>
/// The first column id is always <c>"x"</c> with Unix-millisecond timestamps in strictly ascending order,
/// followed by one array per data series. Client chart parsers (DrKLO <c>ChartData.measure</c>,
/// <c>StackBarChartData</c>) require at least 2 x points and at least one aligned data column and crash
/// otherwise, so <see cref="BuildInlineAsync"/> emits a <c>statsGraphError</c> for any spec with fewer
/// than 2 x points, no data series, or a series whose length differs from the x axis.
/// <see cref="SerializeGraphJson"/> itself stays total: it serializes any well-formed spec verbatim.
/// </remarks>
public sealed class GraphBuilder(IAsyncGraphStore asyncGraphStore) : IGraphBuilder, ISingletonDependency
{
    /// <summary>Light-theme palette keyed by <see cref="GraphSeries.ColorKey"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary"] = "#2196F3",
            ["secondary"] = "#4CAF50",
            ["tertiary"] = "#FF9800",
            ["quaternary"] = "#E53935",
            ["quinary"] = "#9C27B0",
            ["default"] = "#5A9BD4"
        };

    /// <summary>Dark-theme palette keyed by <see cref="GraphSeries.ColorKey"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary"] = "#64B5F6",
            ["secondary"] = "#81C784",
            ["tertiary"] = "#FFB74D",
            ["quaternary"] = "#EF5350",
            ["quinary"] = "#BA68C8",
            ["default"] = "#7CB5EC"
        };

    private const string DefaultColorKey = "default";

    /// <summary>User-visible text shown in the chart slot when a graph has too little data to render.</summary>
    private const string NoDataError = "Not enough data to display this graph.";

    public async Task<IStatsGraph> BuildInlineAsync(GraphSpec spec, bool dark, string snapshotId, int nowUnix)
    {
        try
        {
            // Client chart parsers construct the chart before validating it and crash on x.length < 2,
            // zero data columns, or columns misaligned with the x axis — never wrap such a spec into a
            // statsGraph.
            var xCount = spec?.XAxisMillis?.Count ?? 0;
            if (xCount < 2
                || spec!.Series is not { Count: > 0 }
                || spec.Series.Any(s => s?.Values is null || s.Values.Count != xCount))
            {
                return new TStatsGraphError { Error = NoDataError };
            }

            // An all-zero graph carries no information: clients render an empty card, and
            // DoubleLinearChartData divides the series maxima by each other (a zero series yields
            // Infinity scaling). Report "no data" instead, which clients render as explanatory text.
            if (spec.Series.All(s => s.Values.All(v => v == 0)))
            {
                return new TStatsGraphError { Error = NoDataError };
            }

            var json = SerializeGraphJson(spec, dark);
            var graph = new TStatsGraph
            {
                Json = new TDataJSON { Data = json }
            };

            // When the spec has an associated zoomed series, register it in the Async_Graph_Store and
            // carry the returned (non-empty) token as zoom_token; otherwise leave zoom_token unset
            // (Requirement 8.5). The token resolves to the zoomed series when supplied with its zoom x.
            if (spec.Zoom != null)
            {
                graph.ZoomToken = await asyncGraphStore.IssueTokenAsync(spec, snapshotId, dark, spec.Zoom, nowUnix);
            }

            return graph;
        }
        catch (Exception ex)
        {
            return new TStatsGraphError { Error = "STATS_GRAPH_SERIALIZE_FAILED: " + ex.Message };
        }
    }

    public async Task<IStatsGraph> BuildAsyncPlaceholderAsync(GraphSpec spec, bool dark, string snapshotId, int nowUnix)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Register the spec (with any zoomed series) and return a statsGraphAsync carrying the opaque,
        // non-empty token issued by the Async_Graph_Store (Requirements 8.5, 9.1).
        var token = await asyncGraphStore.IssueTokenAsync(spec, snapshotId, dark, spec.Zoom, nowUnix);
        return new TStatsGraphAsync { Token = token };
    }

    public string SerializeGraphJson(GraphSpec spec, bool dark)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.XAxisMillis);
        ArgumentNullException.ThrowIfNull(spec.Series);

        var columns = new JsonArray();

        // First column is always the x axis: ["x", t0, t1, ...].
        var xColumn = new JsonArray { (JsonNode)"x" };
        foreach (var millis in spec.XAxisMillis)
        {
            xColumn.Add(millis);
        }

        columns.Add(xColumn);

        var types = new JsonObject { ["x"] = "x" };
        var names = new JsonObject();
        var colors = new JsonObject();

        var typeName = KindToType(spec.Kind);

        foreach (var series in spec.Series)
        {
            ArgumentNullException.ThrowIfNull(series);
            ArgumentNullException.ThrowIfNull(series.Values);

            var column = new JsonArray { (JsonNode)series.Id };
            foreach (var value in series.Values)
            {
                column.Add(value);
            }

            columns.Add(column);
            types[series.Id] = typeName;
            names[series.Id] = series.Name;
            colors[series.Id] = ResolveColor(series.ColorKey, dark);
        }

        var root = new JsonObject
        {
            ["columns"] = columns,
            ["types"] = types,
            ["names"] = names,
            ["colors"] = colors
        };

        return root.ToJsonString();
    }

    public GraphSpec? ParseGraphJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return null;
            }

            if (root["columns"] is not JsonArray columns || columns.Count == 0)
            {
                return null;
            }

            var types = root["types"] as JsonObject;
            var names = root["names"] as JsonObject;
            var colors = root["colors"] as JsonObject;

            // First column must be the x axis.
            if (columns[0] is not JsonArray xColumn
                || xColumn.Count == 0
                || xColumn[0]?.GetValue<string>() != "x")
            {
                return null;
            }

            var xAxis = new List<long>(xColumn.Count - 1);
            for (var i = 1; i < xColumn.Count; i++)
            {
                xAxis.Add(xColumn[i]!.GetValue<long>());
            }

            var series = new List<GraphSeries>(columns.Count - 1);
            var kind = GraphKind.Line;
            var kindResolved = false;

            for (var c = 1; c < columns.Count; c++)
            {
                if (columns[c] is not JsonArray column || column.Count == 0)
                {
                    return null;
                }

                var id = column[0]?.GetValue<string>();
                if (id is null)
                {
                    return null;
                }

                var values = new List<long>(column.Count - 1);
                for (var i = 1; i < column.Count; i++)
                {
                    values.Add(column[i]!.GetValue<long>());
                }

                if (!kindResolved)
                {
                    kind = TypeToKind(types?[id]?.GetValue<string>());
                    kindResolved = true;
                }

                var name = names?[id]?.GetValue<string>() ?? id;
                var colorKey = colors?[id]?.GetValue<string>() ?? DefaultColorKey;

                series.Add(new GraphSeries(id, name, colorKey, values));
            }

            return new GraphSpec(kind, xAxis, series);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a <see cref="GraphSeries.ColorKey"/> to a hex color. A value that is already a hex color
    /// (e.g. produced by a previous serialization and fed back through <see cref="ParseGraphJson"/>) is
    /// returned unchanged so the JSON round-trips; otherwise the palette entry for the key is used, falling
    /// back to the palette's <c>default</c> entry for unknown keys.
    /// </summary>
    private static string ResolveColor(string colorKey, bool dark)
    {
        var palette = dark ? DarkPalette : LightPalette;

        if (!string.IsNullOrEmpty(colorKey) && IsHexColor(colorKey))
        {
            return colorKey;
        }

        if (!string.IsNullOrEmpty(colorKey) && palette.TryGetValue(colorKey, out var color))
        {
            return color;
        }

        return palette[DefaultColorKey];
    }

    private static bool IsHexColor(string value)
    {
        if (value.Length is not (4 or 7) || value[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            var isHex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string KindToType(GraphKind kind) => kind switch
    {
        GraphKind.Line => "line",
        GraphKind.Bar => "bar",
        GraphKind.StackedBar => "bar",
        GraphKind.Area => "area",
        GraphKind.Step => "step",
        GraphKind.Pie => "line",
        _ => "line"
    };

    private static GraphKind TypeToKind(string? type) => type switch
    {
        "line" => GraphKind.Line,
        "bar" => GraphKind.Bar,
        "area" => GraphKind.Area,
        "step" => GraphKind.Step,
        _ => GraphKind.Line
    };
}
