namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The chart kind emitted in the Statistics_Graph_JSON <c>types</c> map for each data-series column.
/// </summary>
public enum GraphKind
{
    Line,
    Bar,
    StackedBar,
    Area,
    Step,
    Pie
}

/// <summary>
/// A single data-series column of a statistics graph.
/// </summary>
/// <param name="Id">The column id used as the key in the <c>columns</c>/<c>types</c>/<c>names</c>/<c>colors</c> maps.</param>
/// <param name="Name">The human-readable series label emitted in <c>names</c>.</param>
/// <param name="ColorKey">The palette key resolved to a light- or dark-theme color emitted in <c>colors</c>.</param>
/// <param name="Values">The per-point values, aligned by index with the graph's x-axis.</param>
public sealed record GraphSeries(string Id, string Name, string ColorKey, IReadOnlyList<long> Values);

/// <summary>
/// A fully-described statistics graph ready to be serialized into Statistics_Graph_JSON.
/// </summary>
/// <param name="Kind">The chart kind for the produced columns.</param>
/// <param name="XAxisMillis">The x-axis Unix-millisecond timestamps in strictly ascending order.</param>
/// <param name="Series">The data-series columns (one array per series in the produced JSON).</param>
/// <param name="Zoom">An optional zoomed series that produces a non-empty <c>zoom_token</c> when present.</param>
/// <param name="PairedScale">
/// <see langword="true"/> when the target slot is one the clients render with <c>DoubleLinearChartData</c>
/// — the interaction pairs (<c>interactions_graph</c>, <c>iv_interactions_graph</c>,
/// <c>story_interactions_graph</c>) and the supergroup <c>actions_graph</c>, which
/// <c>StatisticActivity</c> passes as graph type 1. That parser scales each series against the largest
/// one's maximum (<c>linesK[i] = max / maxValue</c>), so a series with no positive value divides by zero.
/// The flag cannot be inferred from <see cref="Kind"/>: <c>followers_graph</c> is a two-series line chart
/// too, but the client reads it as graph type 0 (plain <c>ChartData</c>), where a zero series is fine.
/// </param>
public sealed record GraphSpec(
    GraphKind Kind,
    IReadOnlyList<long> XAxisMillis,
    IReadOnlyList<GraphSeries> Series,
    GraphSpec? Zoom = null,
    bool PairedScale = false);
