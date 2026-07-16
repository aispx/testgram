using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Property 5: Graph colors follow the requested theme palette.
///
/// For any metrics series, when the <c>dark</c> flag is set every produced graph column color is drawn
/// from the dark-theme palette, and when it is not set every color is drawn from the light-theme palette.
///
/// The shared <see cref="StatsGen.GraphSpec"/> generator draws each series' <c>ColorKey</c> from the
/// palette key set and emits empty, single-series, and multi-series shapes, so this single property
/// exercises all of them. The fixture's own <c>Dark</c> flag is ignored here; the same spec is serialized
/// once with <c>dark = true</c> and once with <c>dark = false</c> so both palettes are checked against
/// identical inputs. Each run executes a minimum of 100 generated cases.
///
/// **Validates: Requirements 2.6, 2.7, 3.6, 4.5**
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class GraphThemePalettePropertyTests
{
    private static readonly IReadOnlySet<string> LightPaletteColors =
        GraphBuilder.LightPalette.Values.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> DarkPaletteColors =
        GraphBuilder.DarkPalette.Values.ToHashSet(StringComparer.Ordinal);

    [Property]
    public void Graph_colors_follow_the_requested_theme_palette(GraphSpecFixture fixture)
    {
        var builder = new GraphBuilder(new FakeAsyncGraphStore());
        var spec = ToProductionSpec(fixture);

        // dark = true -> every color must come from the dark-theme palette (Requirements 2.6, 3.6, 4.5).
        AssertColorsDrawnFrom(builder.SerializeGraphJson(spec, dark: true), DarkPaletteColors, "dark");

        // dark = false -> every color must come from the light-theme palette (Requirement 2.7).
        AssertColorsDrawnFrom(builder.SerializeGraphJson(spec, dark: false), LightPaletteColors, "light");
    }

    private static void AssertColorsDrawnFrom(string json, IReadOnlySet<string> palette, string theme)
    {
        var root = JsonNode.Parse(json).ShouldBeOfType<JsonObject>();
        var colors = root["colors"].ShouldBeOfType<JsonObject>();

        foreach (var (columnId, colorNode) in colors)
        {
            var color = colorNode!.GetValue<string>();
            palette.ShouldContain(
                color,
                $"the {theme}-theme color for column '{columnId}' must be drawn from the {theme}-theme palette");
        }
    }

    private static GraphSpec ToProductionSpec(GraphSpecFixture fixture)
    {
        var series = fixture.Series
            .Select(s => new GraphSeries(s.Id, s.Name, s.ColorKey, s.Values.ToList()))
            .ToList();

        var zoom = fixture.Zoom is null ? null : ToProductionSpec(fixture.Zoom);

        return new GraphSpec(GraphKind.Line, fixture.XAxisMillis.ToList(), series, zoom);
    }
}
