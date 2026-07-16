using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Graph_Builder: serializes a metrics series into the Telegram Statistics_Graph_JSON format and
/// produces <c>statsGraph</c>, <c>statsGraphAsync</c>, or <c>statsGraphError</c> objects with theme colors.
/// </summary>
public interface IGraphBuilder
{
    /// <summary>
    /// Builds an inline <c>statsGraph</c> (or <c>statsGraphError</c> on serialization failure) for the spec.
    /// When <see cref="GraphSpec.Zoom"/> is present, a non-empty <c>zoom_token</c> is registered in the
    /// Async_Graph_Store and set on the produced <c>statsGraph</c>; otherwise <c>zoom_token</c> is left unset.
    /// </summary>
    /// <param name="spec">The graph to serialize.</param>
    /// <param name="dark">Whether to use the dark-theme palette.</param>
    /// <param name="snapshotId">The statistics snapshot the graph belongs to (used for zoom-token issuance).</param>
    /// <param name="nowUnix">The current server time in Unix seconds (used for zoom-token issuance).</param>
    Task<IStatsGraph> BuildInlineAsync(GraphSpec spec, bool dark, string snapshotId, int nowUnix);

    /// <summary>
    /// Builds a <c>statsGraphAsync</c> placeholder carrying a non-empty token registered in the
    /// Async_Graph_Store for later resolution via <c>stats.loadAsyncGraph</c>.
    /// </summary>
    /// <param name="spec">The graph to defer.</param>
    /// <param name="dark">The theme captured at issue time.</param>
    /// <param name="snapshotId">The statistics snapshot the graph belongs to.</param>
    /// <param name="nowUnix">The current server time in Unix seconds.</param>
    Task<IStatsGraph> BuildAsyncPlaceholderAsync(GraphSpec spec, bool dark, string snapshotId, int nowUnix);

    /// <summary>
    /// Serializes the spec into a Statistics_Graph_JSON string.
    /// </summary>
    string SerializeGraphJson(GraphSpec spec, bool dark);

    /// <summary>
    /// Parses a Statistics_Graph_JSON string back into a <see cref="GraphSpec"/> (inverse of
    /// <see cref="SerializeGraphJson"/>), or <c>null</c> when the input is not a valid document.
    /// </summary>
    GraphSpec? ParseGraphJson(string json);
}
