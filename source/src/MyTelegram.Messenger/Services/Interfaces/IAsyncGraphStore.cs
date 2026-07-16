using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Async_Graph_Store: issues opaque async graph tokens and resolves them, enforcing the fixed
/// precedence of recognition, validity window (86,400 s), and snapshot currency, plus zoom <c>x</c> lookup.
/// </summary>
public interface IAsyncGraphStore
{
    /// <summary>
    /// Persists the spec (with optional zoom, snapshot id, theme, and issue time) and returns a new opaque token.
    /// </summary>
    Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix);

    /// <summary>
    /// Resolves a token (and optional zoom <paramref name="x"/>) at <paramref name="nowUnix"/>, returning the
    /// resolution outcome and, on success, the graph spec to serialize.
    /// </summary>
    Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix);
}
