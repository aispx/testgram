using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 12: Async graph tokens round-trip, including zoom.
///
/// For any graph spec designated for async loading, <c>BuildAsyncPlaceholderAsync</c> returns a
/// <c>statsGraphAsync</c> with a non-empty token, and resolving that token via <c>loadAsyncGraph</c>
/// returns a <c>statsGraph</c> whose JSON equals the spec's serialization; and for any spec that has an
/// associated zoomed series, the produced <c>statsGraph</c> carries a non-empty <c>zoom_token</c> that,
/// supplied back with its zoom <c>x</c>, resolves to the zoomed series, while a spec without a zoomed
/// series leaves <c>zoom_token</c> unset.
///
/// **Validates: Requirements 8.5, 9.1, 9.2, 9.3**
///
/// Per the tasks.md testing notes, storage property tests run against an in-memory store rather than a
/// real MongoDB. <see cref="InMemoryAsyncGraphStore"/> (nested below) faithfully mirrors the production
/// <see cref="AsyncGraphStore"/> issue+resolve semantics — it persists each issued token by serialising
/// the <see cref="GraphSpec"/> (and its optional zoom) to JSON with the same options the real store uses,
/// enforces the fixed recognition -> expiry (86,400 s) -> currency precedence, and resolves the zoom
/// <c>x</c> — so any loss of round-trip fidelity in the spec's persistence would surface here. The shared
/// <see cref="StatsGen.GraphSpec"/> generator emits empty, single-series, multi-series, and zoom cases
/// (with the theme flag toggled), so this single property exercises both the "has zoom" and "no zoom"
/// branches. Each run executes a minimum of 100 generated cases.
///
/// A degenerate fixture (fewer than 2 x points) still issues and resolves tokens, but the final
/// serialization yields a <c>statsGraphError</c> — the Graph_Builder refuses to emit a
/// client-crashing <c>statsGraph</c> — so the JSON-equality and zoom-token assertions apply only to
/// renderable fixtures.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class AsyncTokenRoundTripPropertyTests
{
    // A fixed issue/resolve time (2023-08-01 00:00:00 UTC). Issuing and resolving at the same instant keeps
    // every token well within its 86,400-second validity window, isolating this property from expiry.
    private const int Now = 1_690_848_000;

    [Property]
    public void Async_graph_tokens_round_trip_including_zoom(GraphSpecFixture fixture)
    {
        var store = new InMemoryAsyncGraphStore();
        var builder = new GraphBuilder(store);

        var spec = ToProductionSpec(fixture);
        var dark = fixture.Dark;

        // The JSON the spec serialises to inline; the async round-trip must reproduce exactly this.
        var expectedMainJson = builder.SerializeGraphJson(spec, dark);

        // ---- Part 1: an async placeholder token round-trips to the main spec (Requirements 9.1, 9.2) ----

        var placeholder = builder
            .BuildAsyncPlaceholderAsync(spec, dark, NewSnapshotId("async"), Now)
            .GetAwaiter().GetResult();

        var async = placeholder.ShouldBeOfType<TStatsGraphAsync>();
        async.Token.ShouldNotBeNullOrEmpty(
            "BuildAsyncPlaceholderAsync must return a statsGraphAsync carrying a non-empty token (Req 9.1)");

        var resolution = store.ResolveAsync(async.Token, x: null, Now).GetAwaiter().GetResult();
        resolution.Status.ShouldBe(
            AsyncGraphStatus.Ok, "a freshly-issued, current token must resolve successfully (Req 9.2)");
        resolution.Spec.ShouldNotBeNull();

        // Resolving the token yields a statsGraph whose JSON equals the spec's own serialisation (Req 9.2).
        var resolvedGraph = builder
            .BuildInlineAsync(resolution.Spec!, resolution.Dark, NewSnapshotId("resolved"), Now)
            .GetAwaiter().GetResult();

        // A degenerate spec (x.length < 2) round-trips through the store, but the final serialization
        // refuses to emit a client-crashing statsGraph and yields a statsGraphError instead; the
        // JSON-equality and zoom assertions below only apply to renderable specs.
        if (fixture.XAxisMillis.Count < 2)
        {
            var degenerate = resolvedGraph.ShouldBeOfType<TStatsGraphError>();
            degenerate.Error.ShouldNotBeNullOrEmpty();
            return;
        }

        var resolvedInline = resolvedGraph.ShouldBeOfType<TStatsGraph>();
        resolvedInline.Json.Data.ShouldBe(
            expectedMainJson,
            "resolving an async token must yield a graph whose JSON equals the spec's serialization");

        // ---- Part 2: zoom_token behaviour on the inline statsGraph (Requirements 8.5, 9.3) ----

        var inline = builder
            .BuildInlineAsync(spec, dark, NewSnapshotId("inline"), Now)
            .GetAwaiter().GetResult();
        var inlineGraph = inline.ShouldBeOfType<TStatsGraph>();

        if (fixture.Zoom is null)
        {
            // A spec without a zoomed series leaves zoom_token unset (Requirement 8.5).
            inlineGraph.ZoomToken.ShouldBeNull(
                "a spec without a zoomed series must leave zoom_token unset (Req 8.5)");
        }
        else
        {
            // A spec with a zoomed series carries a non-empty zoom_token (Requirement 8.5).
            inlineGraph.ZoomToken.ShouldNotBeNullOrEmpty(
                "a spec with a zoomed series must carry a non-empty zoom_token (Req 8.5)");

            // Supplying that zoom_token back with a zoom x resolves to the zoomed series (Requirement 9.3).
            var zoomResolution = store.ResolveAsync(inlineGraph.ZoomToken!, x: 0L, Now).GetAwaiter().GetResult();
            zoomResolution.Status.ShouldBe(
                AsyncGraphStatus.Ok, "the zoom token supplied with its x must resolve successfully (Req 9.3)");
            zoomResolution.Spec.ShouldNotBeNull();

            var zoomSpec = ToProductionSpec(fixture.Zoom);
            var expectedZoomJson = builder.SerializeGraphJson(zoomSpec, dark);

            var zoomGraph = builder
                .BuildInlineAsync(zoomResolution.Spec!, zoomResolution.Dark, NewSnapshotId("zoom"), Now)
                .GetAwaiter().GetResult();
            var zoomInline = zoomGraph.ShouldBeOfType<TStatsGraph>();
            zoomInline.Json.Data.ShouldBe(
                expectedZoomJson,
                "resolving the zoom token with its x must yield the zoomed series' graph (Req 9.3)");
        }
    }

    /// <summary>
    /// A unique snapshot id in its own scope per issuance so that issuing successive tokens in one test
    /// never marks an earlier token's snapshot as no longer current (which would otherwise resolve as
    /// Outdated). Format matches the store's <c>"{scope}:{version}"</c> convention.
    /// </summary>
    private static string NewSnapshotId(string prefix) => $"{prefix}:{Guid.NewGuid():N}:1";

    /// <summary>
    /// Maps a shared graph fixture (empty / single- / multi-series, with or without zoom) onto the
    /// production <see cref="GraphSpec"/>. The fixture carries no chart kind, so <see cref="GraphKind.Line"/>
    /// is used; a present zoom fixture is mapped recursively so the produced spec drives zoom-token issuance.
    /// </summary>
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
    /// <see cref="AsyncGraphStore"/> issue+resolve semantics without MongoDB. Each issued token persists a
    /// JSON serialization of the <see cref="GraphSpec"/> (and its optional zoom) using the same options the
    /// real store uses, so a round-trip through resolution reflects the real persistence path. Resolution
    /// enforces the fixed precedence of token recognition -> validity-window expiry (86,400 s) -> snapshot
    /// currency, then resolves the zoom <c>x</c> (a non-null <c>x</c> without a zoom series is
    /// <see cref="AsyncGraphStatus.ZoomInvalid"/>). Issuing a token advances the current-snapshot pointer
    /// for its scope, exactly as the production store does.
    /// </summary>
    private sealed class InMemoryAsyncGraphStore : IAsyncGraphStore
    {
        private const int ValidityWindowSeconds = 86_400;

        // Mirrors AsyncGraphStore.SpecJsonOptions: GraphSpec is an immutable record bound by constructor
        // parameter name, so serialization must round-trip case-insensitively.
        private static readonly JsonSerializerOptions SpecJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, Entry> _issued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _currentSnapshotByScope = new(StringComparer.Ordinal);

        public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(snapshotId);

            var token = Guid.NewGuid().ToString("N");
            _issued[token] = new Entry(
                JsonSerializer.Serialize(spec, SpecJsonOptions),
                zoom is null ? null : JsonSerializer.Serialize(zoom, SpecJsonOptions),
                dark,
                snapshotId,
                nowUnix);

            _currentSnapshotByScope[GetSnapshotScope(snapshotId)] = snapshotId;
            return Task.FromResult(token);
        }

        public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix)
        {
            // Precedence step 1: token recognition (Requirement 9.4 / 9.9).
            if (string.IsNullOrEmpty(token) || !_issued.TryGetValue(token, out var entry))
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false));
            }

            // Precedence step 2: validity-window expiry (Requirement 9.5 / 9.9).
            if (entry.IssuedAt + ValidityWindowSeconds < nowUnix)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Expired, null, entry.Dark));
            }

            // Precedence step 3: snapshot currency (Requirement 9.6 / 9.9).
            var scope = GetSnapshotScope(entry.SnapshotId);
            if (_currentSnapshotByScope.TryGetValue(scope, out var current) && current != entry.SnapshotId)
            {
                return Task.FromResult(new AsyncGraphResolution(AsyncGraphStatus.Outdated, null, entry.Dark));
            }

            // Zoom resolution (Requirements 9.3 / 9.8).
            if (x is not null)
            {
                var zoomSpec = entry.ZoomJson is null ? null : DeserializeSpec(entry.ZoomJson);
                return Task.FromResult(zoomSpec is null
                    ? new AsyncGraphResolution(AsyncGraphStatus.ZoomInvalid, null, entry.Dark)
                    : new AsyncGraphResolution(AsyncGraphStatus.Ok, zoomSpec, entry.Dark));
            }

            var spec = DeserializeSpec(entry.SpecJson);
            return Task.FromResult(spec is null
                ? new AsyncGraphResolution(AsyncGraphStatus.Invalid, null, false)
                : new AsyncGraphResolution(AsyncGraphStatus.Ok, spec, entry.Dark));
        }

        private static GraphSpec? DeserializeSpec(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<GraphSpec>(json, SpecJsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Matches AsyncGraphStore.GetSnapshotScope: scope is everything before the final ':'.
        private static string GetSnapshotScope(string snapshotId)
        {
            var index = snapshotId.LastIndexOf(':');
            return index <= 0 ? snapshotId : snapshotId[..index];
        }

        private sealed record Entry(string SpecJson, string? ZoomJson, bool Dark, string SnapshotId, int IssuedAt);
    }
}
