using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 4.6 — example/edge-case unit tests for the Async_Graph_Store resolution
/// error branches and the post-resolution graph-serialization failure.
///
/// <para>These complement the async-graph property tasks (Properties 12/13/14) by pinning down the
/// individual error outcomes of <see cref="IAsyncGraphStore.ResolveAsync"/> and how they map to the
/// documented RPC errors:</para>
/// <list type="bullet">
///   <item>A token issued more than the validity window (86,400 s) before the current server time resolves
///   to <see cref="AsyncGraphStatus.Expired"/> → <c>GRAPH_EXPIRED_RELOAD</c> (Requirement 9.5).</item>
///   <item>A token whose statistics snapshot scope has since been superseded resolves to
///   <see cref="AsyncGraphStatus.Outdated"/> → <c>GRAPH_OUTDATED_RELOAD</c> (Requirement 9.6).</item>
///   <item>A non-null zoom <c>x</c> with no available zoomed series resolves to
///   <see cref="AsyncGraphStatus.ZoomInvalid"/> → <c>GRAPH_INVALID_RELOAD</c> (Requirement 9.8).</item>
///   <item>When a valid token resolves but the resolved spec cannot be serialized, the Graph_Builder
///   returns a <c>statsGraphError</c> carrying an error string, never a partial <c>statsGraph</c>
///   (Requirement 9.7 — a Graph_Builder concern exercised where serialization fails).</item>
/// </list>
///
/// <para>Per the tasks.md notes, storage tests avoid a real MongoDB in the test loop. This file uses a
/// faithful in-memory <see cref="IAsyncGraphStore"/> (<see cref="FaithfulInMemoryAsyncGraphStore"/>) that
/// mirrors the real <see cref="AsyncGraphStore"/> semantics exactly: the fixed precedence of token
/// recognition → validity-window expiry (<c>issuedAt + 86400 &lt; now</c>) → snapshot currency, plus the
/// zoom <c>x</c> lookup and the same snapshot-scope derivation.</para>
/// </summary>
public class AsyncGraphErrorBranchTests
{
    private const int ValidityWindowSeconds = AsyncGraphStore.ValidityWindowSeconds; // 86,400

    private static GraphSpec SampleSpec() => new(
        GraphKind.Line,
        new List<long> { 1_690_848_000_000, 1_690_934_400_000 },
        new List<GraphSeries> { new("y0", "Views", "primary", new List<long> { 1, 2 }) });

    private static GraphSpec ZoomSpec() => new(
        GraphKind.Bar,
        new List<long> { 1_690_848_000_000 },
        new List<GraphSeries> { new("z0", "Zoomed", "secondary", new List<long> { 7 }) });

    // ----- GRAPH_EXPIRED_RELOAD: issued more than 86,400 s ago (Requirement 9.5) -----

    [Fact]
    public async Task Token_issued_more_than_the_validity_window_ago_resolves_as_Expired()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 1_000_000;
        var token = await store.IssueTokenAsync(SampleSpec(), "channel:100:v1", dark: false, zoom: null, nowUnix: issuedAt);

        // now is one second beyond the validity window: issuedAt + 86400 < now.
        var resolution = await store.ResolveAsync(token, x: null, nowUnix: issuedAt + ValidityWindowSeconds + 1);

        resolution.Status.ShouldBe(AsyncGraphStatus.Expired);
        resolution.Spec.ShouldBeNull();
    }

    [Fact]
    public async Task Token_at_exactly_the_validity_window_boundary_is_not_yet_Expired()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 1_000_000;
        var token = await store.IssueTokenAsync(SampleSpec(), "channel:100:v1", dark: false, zoom: null, nowUnix: issuedAt);

        // At exactly issuedAt + 86400 the token is still valid (strict less-than expiry check).
        var resolution = await store.ResolveAsync(token, x: null, nowUnix: issuedAt + ValidityWindowSeconds);

        resolution.Status.ShouldBe(AsyncGraphStatus.Ok);
        resolution.Spec.ShouldNotBeNull();
    }

    // ----- GRAPH_OUTDATED_RELOAD: snapshot scope superseded (Requirement 9.6) -----

    [Fact]
    public async Task Token_whose_snapshot_scope_has_been_superseded_resolves_as_Outdated()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 2_000_000;

        // Issue against v1 of the "channel:100" scope, then issue a newer token for the same scope (v2),
        // which advances the current snapshot pointer for that scope.
        var oldToken = await store.IssueTokenAsync(SampleSpec(), "channel:100:v1", dark: false, zoom: null, nowUnix: issuedAt);
        await store.IssueTokenAsync(SampleSpec(), "channel:100:v2", dark: false, zoom: null, nowUnix: issuedAt + 10);

        var resolution = await store.ResolveAsync(oldToken, x: null, nowUnix: issuedAt + 20);

        resolution.Status.ShouldBe(AsyncGraphStatus.Outdated);
        resolution.Spec.ShouldBeNull();
    }

    [Fact]
    public async Task Token_whose_snapshot_is_still_current_resolves_as_Ok()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 2_000_000;
        var token = await store.IssueTokenAsync(SampleSpec(), "channel:100:v1", dark: false, zoom: null, nowUnix: issuedAt);

        var resolution = await store.ResolveAsync(token, x: null, nowUnix: issuedAt + 20);

        resolution.Status.ShouldBe(AsyncGraphStatus.Ok);
        resolution.Spec.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expiry_is_evaluated_before_snapshot_currency()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 3_000_000;

        // Old token is both expired AND superseded; the fixed precedence returns Expired first.
        var oldToken = await store.IssueTokenAsync(SampleSpec(), "channel:200:v1", dark: false, zoom: null, nowUnix: issuedAt);
        await store.IssueTokenAsync(SampleSpec(), "channel:200:v2", dark: false, zoom: null, nowUnix: issuedAt + 10);

        var resolution = await store.ResolveAsync(oldToken, x: null, nowUnix: issuedAt + ValidityWindowSeconds + 1);

        resolution.Status.ShouldBe(AsyncGraphStatus.Expired);
    }

    // ----- GRAPH_INVALID_RELOAD: zoom x with no zoomed series (Requirement 9.8) -----

    [Fact]
    public async Task Non_null_zoom_x_with_no_zoom_series_resolves_as_ZoomInvalid()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 4_000_000;

        // Token issued without any zoomed series.
        var token = await store.IssueTokenAsync(SampleSpec(), "channel:300:v1", dark: false, zoom: null, nowUnix: issuedAt);

        var resolution = await store.ResolveAsync(token, x: 1_690_848_000_000, nowUnix: issuedAt + 20);

        // ZoomInvalid is mapped by the handler to GRAPH_INVALID_RELOAD (Requirement 9.8).
        resolution.Status.ShouldBe(AsyncGraphStatus.ZoomInvalid);
        resolution.Spec.ShouldBeNull();
    }

    [Fact]
    public async Task Non_null_zoom_x_with_an_available_zoom_series_resolves_to_the_zoom_spec()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        const int issuedAt = 4_000_000;

        var token = await store.IssueTokenAsync(SampleSpec(), "channel:300:v1", dark: false, zoom: ZoomSpec(), nowUnix: issuedAt);

        var resolution = await store.ResolveAsync(token, x: 1_690_848_000_000, nowUnix: issuedAt + 20);

        resolution.Status.ShouldBe(AsyncGraphStatus.Ok);
        resolution.Spec.ShouldNotBeNull();
        // The resolved spec is the zoomed series, not the main series.
        resolution.Spec!.Series.ShouldHaveSingleItem().Id.ShouldBe("z0");
    }

    // ----- Post-resolution serialization failure -> statsGraphError (Requirement 9.7) -----

    [Fact]
    public async Task Resolved_token_whose_spec_cannot_be_serialized_yields_statsGraphError()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        var builder = new GraphBuilder(store);
        const int issuedAt = 5_000_000;

        // A spec whose data series has a null Values collection cannot be serialized into a valid document.
        var unserializable = new GraphSpec(
            GraphKind.Line,
            new List<long> { 1_690_848_000_000 },
            new List<GraphSeries> { new("y0", "Views", "primary", null!) });

        var token = await store.IssueTokenAsync(unserializable, "channel:400:v1", dark: false, zoom: null, nowUnix: issuedAt);

        var resolution = await store.ResolveAsync(token, x: null, nowUnix: issuedAt + 20);
        resolution.Status.ShouldBe(AsyncGraphStatus.Ok);
        resolution.Spec.ShouldNotBeNull();

        // The Stats_Service builds the graph inline from the resolved spec; serialization fails, so the
        // Graph_Builder returns a statsGraphError carrying an error string (Requirement 9.7).
        var graph = await builder.BuildInlineAsync(resolution.Spec!, resolution.Dark, "channel:400:v1", issuedAt + 20);

        var error = graph.ShouldBeOfType<TStatsGraphError>();
        error.Error.ShouldNotBeNullOrEmpty();
        graph.ShouldNotBeOfType<TStatsGraph>();
    }

    [Fact]
    public async Task Resolved_token_whose_spec_serializes_yields_statsGraph()
    {
        var store = new FaithfulInMemoryAsyncGraphStore();
        var builder = new GraphBuilder(store);
        const int issuedAt = 5_000_000;

        var token = await store.IssueTokenAsync(SampleSpec(), "channel:400:v1", dark: false, zoom: null, nowUnix: issuedAt);

        var resolution = await store.ResolveAsync(token, x: null, nowUnix: issuedAt + 20);
        var graph = await builder.BuildInlineAsync(resolution.Spec!, resolution.Dark, "channel:400:v1", issuedAt + 20);

        graph.ShouldBeOfType<TStatsGraph>();
    }
}

/// <summary>
/// A faithful in-memory <see cref="IAsyncGraphStore"/> that mirrors the real
/// <see cref="AsyncGraphStore"/> semantics without requiring a MongoDB instance. It preserves the fixed
/// resolution precedence — token recognition (<see cref="AsyncGraphStatus.Invalid"/>) → validity-window
/// expiry (<c>issuedAt + 86400 &lt; now</c> ⇒ <see cref="AsyncGraphStatus.Expired"/>) → snapshot currency
/// (superseded scope ⇒ <see cref="AsyncGraphStatus.Outdated"/>) — plus the zoom <c>x</c> lookup
/// (<see cref="AsyncGraphStatus.ZoomInvalid"/> when no zoomed series exists) and the same snapshot-scope
/// derivation (everything before the final <c>':'</c>).
/// </summary>
internal sealed class FaithfulInMemoryAsyncGraphStore : IAsyncGraphStore
{
    private const int ValidityWindowSeconds = AsyncGraphStore.ValidityWindowSeconds;

    private readonly Dictionary<string, StoredToken> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentSnapshotByScope = new(StringComparer.Ordinal);

    private int _counter;

    public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(snapshotId);

        var token = "token_" + (++_counter);
        _tokens[token] = new StoredToken(spec, zoom, dark, snapshotId, nowUnix);

        // Issuing a token advances the "current" snapshot pointer for its scope, so tokens issued against
        // an earlier snapshot of the same scope resolve as Outdated (Requirement 9.6).
        _currentSnapshotByScope[GetSnapshotScope(snapshotId)] = snapshotId;

        return Task.FromResult(token);
    }

    public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
    {
        // Precedence step 1: token recognition (Requirement 9.4 / 9.9).
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var stored))
        {
            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false));
        }

        // Precedence step 2: validity-window expiry (Requirement 9.5 / 9.9).
        if (stored.IssuedAt + ValidityWindowSeconds < nowUnix)
        {
            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Expired, null, stored.Dark));
        }

        // Precedence step 3: snapshot currency (Requirement 9.6 / 9.9).
        var scope = GetSnapshotScope(stored.SnapshotId);
        if (_currentSnapshotByScope.TryGetValue(scope, out var current) && current != stored.SnapshotId)
        {
            return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Outdated, null, stored.Dark));
        }

        // Zoom resolution (Requirements 9.3 / 9.8): a non-null x must identify an available zoom series.
        if (x is not null)
        {
            return Task.FromResult(stored.Zoom is null
                ? new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, stored.Dark)
                : new AsyncGraphResolution(AsyncGraphStatus.Ok, stored.Zoom, stored.Dark));
        }

        return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Ok, stored.Spec, stored.Dark));
    }

    /// <summary>
    /// Mirrors <c>AsyncGraphStore.GetSnapshotScope</c>: the scope is everything before the final
    /// <c>':'</c>; a snapshot id without a delimiter is its own scope (never superseded).
    /// </summary>
    private static string GetSnapshotScope(string snapshotId)
    {
        var index = snapshotId.LastIndexOf(':');
        return index <= 0 ? snapshotId : snapshotId[..index];
    }

    private readonly record struct StoredToken(GraphSpec Spec, GraphSpec? Zoom, bool Dark, string SnapshotId, int IssuedAt);
}
