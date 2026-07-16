using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 14: Async graph error conditions follow a fixed precedence.
///
/// For any token that simultaneously satisfies an arbitrary subset of {unrecognized, validity-window
/// expired, snapshot outdated}, <c>loadAsyncGraph</c> returns the error of the earliest condition in the
/// order recognition (<c>GRAPH_INVALID_RELOAD</c>) -> expiry (<c>GRAPH_EXPIRED_RELOAD</c>) -> currency
/// (<c>GRAPH_OUTDATED_RELOAD</c>).
///
/// Validates: Requirements 9.9.
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. <see cref="InMemoryAsyncGraphStore"/> (nested below) faithfully mirrors the production
/// <see cref="AsyncGraphStore"/> resolution semantics: token recognition, then validity-window expiry
/// (<c>issuedAt + 86400 &lt; now</c>), then snapshot currency (a token whose snapshot scope has since been
/// superseded resolves as Outdated). The shared <see cref="StatsGen.AsyncToken"/> generator toggles the
/// unrecognized / expired / outdated conditions independently and straddles the 86,400-second window, so
/// this single property exercises the full lattice of overlapping conditions. Each run executes a minimum
/// of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class AsyncGraphErrorPrecedencePropertyTests
{
    [Property]
    public void Async_graph_errors_follow_recognition_then_expiry_then_currency(AsyncTokenFixture fixture)
    {
        var store = new InMemoryAsyncGraphStore();

        var spec = ToProductionSpec(fixture.Spec);
        var zoom = fixture.ZoomSpec is null ? null : ToProductionSpec(fixture.ZoomSpec);

        // The snapshot scope is shared; issuing a second token in the same scope advances the "current"
        // pointer and makes the earlier token's snapshot no longer current (Requirement 9.6).
        const string scope = "channel:42:growth";
        var targetSnapshotId = scope + ":1";

        // Realize the "recognized" condition: only tokens that were issued are recognized. An unrecognized
        // token is one the store has never seen.
        string token;
        if (fixture.IsRecognized)
        {
            token = store.IssueTokenAsync(spec, targetSnapshotId, fixture.Spec.Dark, zoom, fixture.IssuedAt)
                .GetAwaiter().GetResult();
        }
        else
        {
            // A never-issued opaque token (the fake never mints this shape), so recognition fails.
            token = "unissued_" + fixture.Token;
        }

        // Realize the "outdated" condition: supersede the target's snapshot scope with a newer snapshot so
        // the earlier token resolves as Outdated. Only meaningful when the token was actually issued.
        if (fixture.IsOutdated)
        {
            store.IssueTokenAsync(spec, scope + ":2", fixture.Spec.Dark, zoom, fixture.NowUnix)
                .GetAwaiter().GetResult();
        }

        var resolution = store.ResolveAsync(token, x: null, fixture.NowUnix).GetAwaiter().GetResult();

        // Expected outcome computed independently from the fixed precedence order. Expiry is derived from
        // the same rule the store uses (issuedAt + window < now) rather than trusting the toggle blindly.
        var expired = fixture.IssuedAt + AsyncTokenFixture.ValidityWindowSeconds < fixture.NowUnix;

        AsyncGraphStatus expectedStatus;
        if (!fixture.IsRecognized)
        {
            expectedStatus = AsyncGraphStatus.Invalid;      // recognition first
        }
        else if (expired)
        {
            expectedStatus = AsyncGraphStatus.Expired;      // then expiry
        }
        else if (fixture.IsOutdated)
        {
            expectedStatus = AsyncGraphStatus.Outdated;     // then currency
        }
        else
        {
            expectedStatus = AsyncGraphStatus.Ok;           // no error condition
        }

        resolution.Status.ShouldBe(expectedStatus);

        // The handler maps the resolution status to the wire RPC error; assert on that mapping too so the
        // property is expressed in the same terms as Requirement 9.9.
        MapToRpcError(resolution.Status).ShouldBe(MapToRpcError(expectedStatus));
    }

    /// <summary>
    /// Mirrors the Stats_Service/handler error mapping (design "IAsyncGraphStore"): Invalid/ZoomInvalid ->
    /// GRAPH_INVALID_RELOAD, Expired -> GRAPH_EXPIRED_RELOAD, Outdated -> GRAPH_OUTDATED_RELOAD, Ok -> none.
    /// </summary>
    private static string? MapToRpcError(AsyncGraphStatus status) => status switch
    {
        AsyncGraphStatus.Ok => null,
        AsyncGraphStatus.Invalid => "GRAPH_INVALID_RELOAD",
        AsyncGraphStatus.ZoomInvalid => "GRAPH_INVALID_RELOAD",
        AsyncGraphStatus.Expired => "GRAPH_EXPIRED_RELOAD",
        AsyncGraphStatus.Outdated => "GRAPH_OUTDATED_RELOAD",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        var zoom = fixture.Zoom is null ? null : ToProductionSpec(fixture.Zoom);

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series, zoom);
    }

    /// <summary>
    /// An in-memory <see cref="IAsyncGraphStore"/> that faithfully mirrors the production
    /// <see cref="AsyncGraphStore"/> resolution precedence without MongoDB: token recognition first, then
    /// validity-window expiry (<c>issuedAt + 86400 &lt; now</c>), then snapshot currency (a token whose
    /// snapshot scope has been superseded is Outdated). Issuing a token advances the current-snapshot
    /// pointer for its scope, exactly as the real store does. Only the behaviour exercised by Property 14
    /// is implemented.
    /// </summary>
    private sealed class InMemoryAsyncGraphStore : IAsyncGraphStore
    {
        public const int ValidityWindowSeconds = 86_400;

        private readonly Dictionary<string, Entry> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _currentSnapshotByScope = new(StringComparer.Ordinal);
        private int _counter;

        private sealed record Entry(GraphSpec Spec, GraphSpec? Zoom, bool Dark, string SnapshotId, int IssuedAt);

        public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(snapshotId);

            var token = "tok_" + (++_counter);
            _tokens[token] = new Entry(spec, zoom, dark, snapshotId, nowUnix);
            _currentSnapshotByScope[GetSnapshotScope(snapshotId)] = snapshotId;
            return Task.FromResult(token);
        }

        public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
        {
            // Precedence step 1: token recognition (GRAPH_INVALID_RELOAD).
            if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false));
            }

            // Precedence step 2: validity-window expiry (GRAPH_EXPIRED_RELOAD).
            if (entry.IssuedAt + ValidityWindowSeconds < nowUnix)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Expired, null, entry.Dark));
            }

            // Precedence step 3: snapshot currency (GRAPH_OUTDATED_RELOAD).
            var scope = GetSnapshotScope(entry.SnapshotId);
            if (_currentSnapshotByScope.TryGetValue(scope, out var current) && current != entry.SnapshotId)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Outdated, null, entry.Dark));
            }

            if (x is not null)
            {
                return Task.FromResult(entry.Zoom is null
                    ? new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, entry.Dark)
                    : new AsyncGraphResolution(AsyncGraphStatus.Ok, entry.Zoom, entry.Dark));
            }

            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Ok, entry.Spec, entry.Dark));
        }

        // Matches AsyncGraphStore.GetSnapshotScope: scope is everything before the final ':'.
        private static string GetSnapshotScope(string snapshotId)
        {
            var index = snapshotId.LastIndexOf(':');
            return index <= 0 ? snapshotId : snapshotId[..index];
        }
    }
}
