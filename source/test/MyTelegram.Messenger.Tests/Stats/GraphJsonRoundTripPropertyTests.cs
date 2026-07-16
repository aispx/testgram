using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 11: Statistics graph JSON round-trips.
///
/// For any metrics series produced by the Metrics_Store, parsing the generated Statistics_Graph_JSON and
/// re-serializing it yields an equivalent Statistics_Graph_JSON document (columns, types, names, and
/// colors preserved).
///
/// The shared <see cref="StatsGen.GraphSpec"/> generator emits empty, single-series, multi-series, and
/// zoom cases (with the theme flag toggled), so this single property exercises all of them. Each run
/// covers the minimum of 100 generated cases.
///
/// **Validates: Requirements 8.4**
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class GraphJsonRoundTripPropertyTests
{
    [Property]
    public void Statistics_graph_json_round_trips(GraphSpecFixture fixture)
    {
        // The round-trip exercises only SerializeGraphJson/ParseGraphJson, which never touch the
        // Async_Graph_Store, so a stub that fails if called guards against accidental coupling.
        var builder = new GraphBuilder(new UnusedAsyncGraphStore());
        var spec = ToProductionSpec(fixture);

        // Serialize -> parse -> re-serialize.
        var json1 = builder.SerializeGraphJson(spec, fixture.Dark);

        var parsed = builder.ParseGraphJson(json1);
        parsed.ShouldNotBeNull($"a well-formed graph JSON must parse back to a spec: {json1}");

        var json2 = builder.SerializeGraphJson(parsed!, fixture.Dark);

        // The round-trip preserves columns, types, names, and colors (Requirement 8.4).
        AssertGraphJsonEquivalent(json1, json2);
    }

    /// <summary>
    /// Maps a shared graph fixture (empty / multi-series / zoom cases) onto the production
    /// <see cref="GraphSpec"/>. The fixture carries no chart kind, so <see cref="GraphKind.Line"/> is
    /// used; the round-trip property holds independently of the chosen kind because the kind is encoded
    /// in and recovered from the JSON <c>types</c> map.
    /// </summary>
    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series);
    }

    /// <summary>
    /// Asserts two Statistics_Graph_JSON documents are equivalent by comparing each of the four
    /// structural sections (<c>columns</c>, <c>types</c>, <c>names</c>, <c>colors</c>) individually so a
    /// failure points at exactly which section diverged.
    /// </summary>
    private static void AssertGraphJsonEquivalent(string expected, string actual)
    {
        var expectedRoot = JsonNode.Parse(expected)!.AsObject();
        var actualRoot = JsonNode.Parse(actual)!.AsObject();

        foreach (var section in new[] { "columns", "types", "names", "colors" })
        {
            var expectedSection = expectedRoot[section];
            var actualSection = actualRoot[section];

            expectedSection.ShouldNotBeNull($"expected document is missing '{section}'");
            actualSection.ShouldNotBeNull($"round-tripped document is missing '{section}'");

            actualSection!.ToJsonString()
                .ShouldBe(expectedSection!.ToJsonString(), $"'{section}' must be preserved across the round-trip");
        }
    }

    /// <summary>
    /// A stub <see cref="IAsyncGraphStore"/> for the round-trip property. Token issuance/resolution is
    /// irrelevant to JSON round-tripping and must never be reached, so both members throw.
    /// </summary>
    private sealed class UnusedAsyncGraphStore : IAsyncGraphStore
    {
        public Task<string> IssueTokenAsync(GraphSpec spec, string snapshotId, bool dark, GraphSpec? zoom, int nowUnix) =>
            throw new NotSupportedException("The graph JSON round-trip must not issue async tokens.");

        public Task<AsyncGraphResolution> ResolveAsync(string token, long? x, int nowUnix) =>
            throw new NotSupportedException("The graph JSON round-trip must not resolve async tokens.");
    }
}
