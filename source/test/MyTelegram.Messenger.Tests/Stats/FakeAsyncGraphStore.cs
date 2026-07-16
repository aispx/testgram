using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// A minimal in-memory <see cref="IAsyncGraphStore"/> stub for the Graph_Builder property tests
/// (Feature: stats-api). It issues a fresh opaque, non-empty token per call and records the associated
/// spec/zoom so <see cref="ResolveAsync"/> can return it. It is deliberately tiny: the Graph_Builder
/// structure/round-trip properties only exercise inline serialization and, at most, token issuance for
/// zoom cases; the full precedence/expiry semantics of the real store are covered by the Async_Graph_Store
/// property tasks.
/// </summary>
public sealed class FakeAsyncGraphStore : IAsyncGraphStore
{
    private readonly Dictionary<string, (GraphSpec Spec, GraphSpec? Zoom, bool Dark)> _issued =
        new(StringComparer.Ordinal);

    private int _counter;

    public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var token = "token_" + (++_counter);
        _issued[token] = (spec, zoom, dark);
        return Task.FromResult(token);
    }

    public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
    {
        if (token is null || !_issued.TryGetValue(token, out var entry))
        {
            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false));
        }

        var spec = x.HasValue ? entry.Zoom : entry.Spec;
        return Task.FromResult(spec is null
            ? new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, entry.Dark)
            : new AsyncGraphResolution(AsyncGraphStatus.Ok, spec, entry.Dark));
    }
}
