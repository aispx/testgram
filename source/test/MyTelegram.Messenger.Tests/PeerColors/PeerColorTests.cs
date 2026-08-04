using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;
using MyTelegram.Schema.Help;
using MyTelegram.Services.Extensions;

namespace MyTelegram.Messenger.Tests.PeerColors;

/// <summary>
/// Unit tests for the server side of <a href="https://core.telegram.org/api/colors">peer colors</a>.
///
/// These pin down the pieces that were previously missing or wrong: the palettes were duplicated as
/// private statics inside the two help.get*Colors handlers with no boost levels and no hash, so clients
/// re-downloaded the full list on every start and no palette was ever gated behind boosts; and the
/// collectible variant of a peer color was silently dropped on the floor.
/// </summary>
public class PeerColorPaletteProviderTests
{
    private static readonly PeerColorPaletteProvider Provider = new();

    [Fact]
    public void Message_palette_serves_the_base_colors_without_a_color_set()
    {
        // Palette ids 0-6 intentionally carry no color set: clients fall back to their own built-in
        // red/orange/violet/green/cyan/blue/pink for those indexes.
        foreach (var colorId in Enumerable.Range(0, 7))
        {
            var option = Provider.GetOption(colorId, forProfile: false);

            option.ShouldNotBeNull();
            option!.Colors.ShouldBeNull();
            option.DarkColors.ShouldBeNull();
        }
    }

    [Fact]
    public void Base_message_palettes_require_no_boosts()
    {
        // The base colors must stay usable by an unboosted channel, otherwise every channel loses
        // the ability to pick any color at all.
        foreach (var colorId in Enumerable.Range(0, 7))
        {
            var option = Provider.GetOption(colorId, forProfile: false)!;

            option.ChannelMinLevel.ShouldBeNull();
            option.GroupMinLevel.ShouldBeNull();
        }
    }

    [Fact]
    public void Extra_message_palettes_are_gated_behind_a_boost_level()
    {
        // Before the fix ChannelMinLevel/GroupMinLevel were never populated, so BOOSTS_REQUIRED could
        // not be enforced for the palettes that are supposed to need boosting.
        foreach (var colorId in Enumerable.Range(7, 15))
        {
            var option = Provider.GetOption(colorId, forProfile: false)!;

            option.ChannelMinLevel.ShouldBe(1);
            option.GroupMinLevel.ShouldBe(1);
        }
    }

    [Fact]
    public void Message_palette_serves_every_id_from_zero_to_twenty_one()
    {
        Provider.GetMessageColorOptions().Count.ShouldBe(22);
        Provider.GetMessageColorOptions()
            .Select(p => p.ColorId)
            .OrderBy(id => id)
            .ShouldBe(Enumerable.Range(0, 22));
    }

    [Fact]
    public void Profile_palette_serves_every_id_from_zero_to_fifteen_with_profile_sets()
    {
        var options = Provider.GetProfileColorOptions();

        options.Count.ShouldBe(16);
        options.Select(p => p.ColorId).OrderBy(id => id).ShouldBe(Enumerable.Range(0, 16));

        // Profile palettes must use peerColorProfileSet (palette/bg/story colors), not the plain
        // peerColorSet used for message accents.
        foreach (var option in options)
        {
            option.Colors.ShouldBeOfType<TPeerColorProfileSet>();
            option.DarkColors.ShouldBeOfType<TPeerColorProfileSet>();
        }
    }

    [Fact]
    public void Profile_palettes_use_the_app_config_boost_levels()
    {
        // These must agree with channel_profile_bg_icon_level_min / group_profile_bg_icon_level_min,
        // otherwise the level the client shows as required differs from the one the server enforces.
        foreach (var option in Provider.GetProfileColorOptions())
        {
            option.ChannelMinLevel.ShouldBe(7);
            option.GroupMinLevel.ShouldBe(5);
        }
    }

    [Fact]
    public void Unknown_color_id_has_no_option_so_callers_can_reject_it()
    {
        // This is what turns into COLOR_INVALID in account/channels.updateColor.
        Provider.GetOption(22, forProfile: false).ShouldBeNull();
        Provider.GetOption(16, forProfile: true).ShouldBeNull();
        Provider.GetOption(-1, forProfile: false).ShouldBeNull();
        Provider.GetOption(999, forProfile: true).ShouldBeNull();
    }

    [Fact]
    public void Message_and_profile_palettes_are_validated_separately()
    {
        // Id 21 exists for message accents only; accepting it for a profile would hand the client a
        // palette help.getPeerProfileColors never advertised.
        Provider.GetOption(21, forProfile: false).ShouldNotBeNull();
        Provider.GetOption(21, forProfile: true).ShouldBeNull();
    }

    [Fact]
    public void Hash_is_stable_across_calls()
    {
        // The whole point of the hash: a client that stored it must get peerColorsNotModified back
        // on the next call, which only works if the value does not move on its own.
        var options = Provider.GetMessageColorOptions();

        Provider.ComputeHash(options).ShouldBe(Provider.ComputeHash(options));
    }

    [Fact]
    public void Message_and_profile_palettes_hash_differently()
    {
        Provider.ComputeHash(Provider.GetMessageColorOptions())
            .ShouldNotBe(Provider.ComputeHash(Provider.GetProfileColorOptions()));
    }

    [Fact]
    public void Hash_changes_when_a_palette_is_removed()
    {
        // A shrinking palette list has to invalidate the client's cached copy; otherwise a removed
        // color stays visible in the picker forever.
        var full = Provider.GetMessageColorOptions();
        var trimmed = full.Take(full.Count - 1).ToList();

        Provider.ComputeHash(full).ShouldNotBe(Provider.ComputeHash(trimmed));
    }

    [Fact]
    public void Hash_depends_on_palette_order()
    {
        // Order is the order the client renders, so it is part of the cached state.
        var forward = Provider.GetMessageColorOptions();
        var reversed = forward.Reverse().ToList();

        Provider.ComputeHash(forward).ShouldNotBe(Provider.ComputeHash(reversed));
    }

    [Fact]
    public void Empty_palette_list_hashes_to_zero()
    {
        // Zero is the client's "nothing cached" sentinel and an empty list is the one case that may
        // legitimately report it.
        Provider.ComputeHash([]).ShouldBe(0);
    }

    [Fact]
    public void Hash_is_not_zero_for_the_served_palettes()
    {
        // A real palette set colliding with the sentinel would be answered notModified against a
        // client holding no copy at all.
        Provider.ComputeHash(Provider.GetMessageColorOptions()).ShouldNotBe(0);
        Provider.ComputeHash(Provider.GetProfileColorOptions()).ShouldNotBe(0);
    }
}

/// <summary>
/// Boost level thresholds behind the BOOSTS_REQUIRED gate of channels.updateColor. The logic used to
/// be private to premium.getBoostsStatus, so no other feature could enforce a level.
/// </summary>
public class BoostLevelCalculatorTests
{
    private static readonly BoostLevelCalculator Calculator = new(null!);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(10, 4)]
    [InlineData(25, 5)]
    [InlineData(50, 6)]
    [InlineData(100, 7)]
    [InlineData(200, 8)]
    [InlineData(500, 9)]
    [InlineData(1000, 10)]
    [InlineData(5000, 10)]
    public void Level_matches_the_boost_thresholds(int boosts, int expectedLevel)
    {
        Calculator.CalculateLevel(boosts).ShouldBe(expectedLevel);
    }

    [Fact]
    public void Level_never_decreases_as_boosts_grow()
    {
        var levels = Enumerable.Range(0, 1200).Select(Calculator.CalculateLevel).ToList();

        levels.ShouldBe(levels.OrderBy(l => l));
    }

    [Fact]
    public void Boosts_for_level_round_trips_back_to_the_same_level()
    {
        // The number of boosts a level advertises must actually reach that level, or a channel that
        // boosts exactly as much as the client asked still gets BOOSTS_REQUIRED.
        foreach (var level in Enumerable.Range(0, 11))
        {
            Calculator.CalculateLevel(Calculator.GetBoostsForLevel(level)).ShouldBe(level);
        }
    }
}

/// <summary>
/// Conversion of a unique star gift into a collectible peer color, and of the stored domain color
/// back into its TL representation. Collectible colors were previously accepted by the schema but
/// dropped without an error, leaving the account with no color at all.
/// </summary>
public class CollectiblePeerColorTests
{
    private static UniqueStarGiftDocument Gift() =>
        new()
        {
            UniqueId = 4242,
            DocumentId = 111,
            Attributes =
            [
                new UniqueGiftAttribute { Type = "model", DocumentId = 222 },
                new UniqueGiftAttribute { Type = "pattern", DocumentId = 333 },
                new UniqueGiftAttribute
                {
                    Type = "backdrop",
                    CenterColor = 0x112233,
                    EdgeColor = 0x445566
                }
            ]
        };

    [Fact]
    public void Gift_maps_onto_the_collectible_fields_of_the_domain_color()
    {
        var color = CollectiblePeerColorHelper.ToPeerColor(Gift());

        color.CollectibleId.ShouldBe(4242);
        color.GiftEmojiId.ShouldBe(222);
        color.BackgroundEmojiId.ShouldBe(333);
        color.AccentColor.ShouldBe(0x112233);
        color.Colors.ShouldBe([0x112233, 0x445566]);

        // A collectible color has no palette id — it is not one of the help.getPeerColors options.
        color.Color.ShouldBeNull();
    }

    [Fact]
    public void Gift_without_attributes_falls_back_to_its_own_document()
    {
        var color = CollectiblePeerColorHelper.ToPeerColor(new UniqueStarGiftDocument
        {
            UniqueId = 7,
            DocumentId = 555
        });

        color.CollectibleId.ShouldBe(7);
        color.GiftEmojiId.ShouldBe(555);
        color.BackgroundEmojiId.ShouldBe(555);
        color.AccentColor.ShouldBe(0);
    }

    [Fact]
    public void Collectible_color_serializes_as_peer_color_collectible()
    {
        // Clients distinguish a gift-backed color from a palette color by the constructor, so the
        // stored collectible must not come back out as a plain peerColor.
        var result = CollectiblePeerColorHelper.ToPeerColor(Gift()).ToPeerColor();

        var collectible = result.ShouldBeOfType<TPeerColorCollectible>();
        collectible.CollectibleId.ShouldBe(4242);
        collectible.GiftEmojiId.ShouldBe(222);
        collectible.BackgroundEmojiId.ShouldBe(333);
        collectible.AccentColor.ShouldBe(0x112233);
        collectible.Colors.ShouldBe(new TVector<int>([0x112233, 0x445566]));
    }

    [Fact]
    public void Palette_color_still_serializes_as_plain_peer_color()
    {
        var result = new PeerColor(5, 777).ToPeerColor();

        var peerColor = result.ShouldBeOfType<TPeerColor>();
        peerColor.Color.ShouldBe(5);
        peerColor.BackgroundEmojiId.ShouldBe(777);
    }

    [Fact]
    public void Unset_color_serializes_to_nothing()
    {
        // An unset color must stay absent from the user/channel object rather than becoming an
        // empty-but-present color the client would render.
        ((PeerColor?)null).ToPeerColor().ShouldBeNull();
        new PeerColor(null, null).ToPeerColor().ShouldBeNull();
    }

    [Fact]
    public void Collectible_color_with_no_palette_colors_still_serializes()
    {
        // Colors is a non-optional vector in peerColorCollectible; a null would crash the client.
        var result = new PeerColor(null, null, CollectibleId: 9).ToPeerColor();

        var collectible = result.ShouldBeOfType<TPeerColorCollectible>();
        collectible.Colors.ShouldNotBeNull();
        collectible.Colors.Count.ShouldBe(0);
    }
}
