namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The outcome of resolving an Async_Graph_Token.
/// </summary>
public enum AsyncGraphStatus
{
    /// <summary>The token resolved to available graph data.</summary>
    Ok,

    /// <summary>The token is empty, malformed, or unknown (maps to <c>GRAPH_INVALID_RELOAD</c>).</summary>
    Invalid,

    /// <summary>The token is older than its validity window (maps to <c>GRAPH_EXPIRED_RELOAD</c>).</summary>
    Expired,

    /// <summary>The token refers to a snapshot that is no longer current (maps to <c>GRAPH_OUTDATED_RELOAD</c>).</summary>
    Outdated,

    /// <summary>A zoom <c>x</c> value was supplied that has no available zoomed series (maps to <c>GRAPH_INVALID_RELOAD</c>).</summary>
    ZoomInvalid
}

/// <summary>
/// The result of resolving an Async_Graph_Token (optionally with a zoom <c>x</c> value).
/// </summary>
/// <param name="Status">The resolution outcome.</param>
/// <param name="Spec">The resolved graph spec when <see cref="AsyncGraphStatus.Ok"/>, otherwise <c>null</c>.</param>
/// <param name="Dark">The theme captured when the token was issued.</param>
public sealed record AsyncGraphResolution(AsyncGraphStatus Status, GraphSpec? Spec, bool Dark);
