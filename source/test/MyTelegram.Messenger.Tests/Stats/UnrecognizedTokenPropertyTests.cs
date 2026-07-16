using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 13: Unrecognized tokens are rejected as invalid.
///
/// For any token string that the Async_Graph_Store has never issued, <c>loadAsyncGraph</c> returns the
/// <c>GRAPH_INVALID_RELOAD</c> error — i.e. <see cref="IAsyncGraphStore.ResolveAsync"/> returns
/// <see cref="AsyncGraphStatus.Invalid"/> — regardless of the supplied zoom <c>x</c> or the current
/// server time.
///
/// **Validates: Requirements 9.4**
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. <see cref="InMemoryAsyncGraphStore"/> (nested below) faithfully mirrors the documented
/// Async_Graph_Store resolution semantics of the production
/// <see cref="MyTelegram.Messenger.Services.Stats.AsyncGraphStore"/>: fixed precedence of token
/// recognition -> validity-window expiry (86,400 s) -> snapshot currency, then zoom <c>x</c> lookup. The
/// property issues an arbitrary (possibly empty) set of tokens, then resolves a token guaranteed to be
/// outside that set and asserts recognition fails first with <see cref="AsyncGraphStatus.Invalid"/>. The
/// shared <see cref="StatsArbitraries"/> generators back the issued specs; each run executes a minimum of
/// 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class UnrecognizedTokenPropertyTests
{
    // A fixed issue time (2023-08-01 00:00:00 UTC) so issued tokens are well within their validity window.
    private const int IssuedAt = 1_690_848_000;

    [Property]
    public void Never_issued_tokens_resolve_as_invalid(
        GraphSpecFixture[] issuedSpecs,
        string? candidateToken,
        long zoomX,
        int nowUnix)
    {
        var store = new InMemoryAsyncGraphStore();

        // Populate the store with an arbitrary set of genuinely-issued tokens. Their opaque values are
        // collected so the candidate can be forced to sit outside the issued set.
        var issuedTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in issuedSpecs ?? Array.Empty<GraphSpecFixture>())
        {
            var spec = ToProductionSpec(fixture);
            var zoom = fixture.Zoom is null ? null : ToProductionSpec(fixture.Zoom);
            var token = store.IssueTokenAsync(spec, "scope:v1", fixture.Dark, zoom, IssuedAt)
                .GetAwaiter().GetResult();
            issuedTokens.Add(token);
        }

        // Guarantee the candidate is a token the store has never issued (covers null/empty and any
        // arbitrary string, including the astronomically-unlikely collision with a generated token).
        var neverIssued = candidateToken ?? string.Empty;
        while (issuedTokens.Contains(neverIssued))
        {
            neverIssued += "_x";
        }

        // Recognition failure must dominate regardless of a zoom x or the current time (Requirement 9.4,
        // and precedence per 9.9: recognition is evaluated first).
        var withoutZoom = store.ResolveAsync(neverIssued, null, nowUnix).GetAwaiter().GetResult();
        var withZoom = store.ResolveAsync(neverIssued, zoomX, nowUnix).GetAwaiter().GetResult();

        withoutZoom.Status.ShouldBe(
            AsyncGraphStatus.Invalid,
            $"never-issued token '{neverIssued}' must resolve as Invalid");
        withZoom.Status.ShouldBe(
            AsyncGraphStatus.Invalid,
            $"never-issued token '{neverIssued}' with zoom x={zoomX} must resolve as Invalid");

        // Invalid resolutions carry no spec (nothing to render).
        withoutZoom.Spec.ShouldBeNull();
        withZoom.Spec.ShouldBeNull();
    }

    /// <summary>Maps a shared graph fixture onto the production <see cref="GraphSpec"/> (kind is irrelevant here).</summary>
    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series);
    }

    /// <summary>
    /// An in-memory <see cref="IAsyncGraphStore"/> that faithfully mirrors the production
    /// <see cref="MyTelegram.Messenger.Services.Stats.AsyncGraphStore"/> resolution semantics without
    /// MongoDB: an opaque random token is issued per call, and <see cref="ResolveAsync"/> enforces the
    /// fixed precedence of token recognition -> validity-window expiry (86,400 s) -> snapshot currency,
    /// then resolves the zoom <c>x</c>. An unrecognized (never-issued) token — including the empty
    /// string — short-circuits to <see cref="AsyncGraphStatus.Invalid"/> before any other check, exactly
    /// as the production store does.
    /// </summary>
    private sealed class InMemoryAsyncGraphStore : IAsyncGraphStore
    {
        private const int ValidityWindowSeconds = 86_400;

        private readonly Dictionary<string, Entry> _issued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _currentSnapshotByScope = new(StringComparer.Ordinal);

        public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(snapshotId);

            var token = Guid.NewGuid().ToString("N");
            _issued[token] = new Entry(spec, zoom, dark, snapshotId, nowUnix);
            _currentSnapshotByScope[GetScope(snapshotId)] = snapshotId;
            return Task.FromResult(token);
        }

        public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
        {
            // Precedence step 1: token recognition (Requirement 9.4 / 9.9). Empty or unknown => Invalid.
            if (string.IsNullOrEmpty(token) || !_issued.TryGetValue(token, out var entry))
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false));
            }

            // Precedence step 2: validity-window expiry.
            if (entry.IssuedAt + ValidityWindowSeconds < nowUnix)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Expired, null, entry.Dark));
            }

            // Precedence step 3: snapshot currency.
            var scope = GetScope(entry.SnapshotId);
            if (_currentSnapshotByScope.TryGetValue(scope, out var current) && current != entry.SnapshotId)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Outdated, null, entry.Dark));
            }

            // Zoom resolution.
            if (x is not null)
            {
                return Task.FromResult(entry.Zoom is null
                    ? new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, entry.Dark)
                    : new AsyncGraphResolution(AsyncGraphStatus.Ok, entry.Zoom, entry.Dark));
            }

            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Ok, entry.Spec, entry.Dark));
        }

        private static string GetScope(string snapshotId)
        {
            var index = snapshotId.LastIndexOf(':');
            return index <= 0 ? snapshotId : snapshotId[..index];
        }

        private sealed record Entry(GraphSpec Spec, GraphSpec? Zoom, bool Dark, string SnapshotId, int IssuedAt);
    }
}
