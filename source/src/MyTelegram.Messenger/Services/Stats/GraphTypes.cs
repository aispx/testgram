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
public sealed record GraphSpec(
    GraphKind Kind,
    IReadOnlyList<long> XAxisMillis,
    IReadOnlyList<GraphSeries> Series,
    GraphSpec? Zoom = null);
