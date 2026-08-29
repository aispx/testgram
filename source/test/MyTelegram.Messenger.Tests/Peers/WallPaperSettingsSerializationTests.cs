using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using System.Buffers;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: serializing <c>wallPaperSettings</c>.
///
/// <para>
/// Telegram's schema gives <c>second_background_color</c> and <c>rotation</c> the <b>same</b> flag bit —
/// both are <c>flags.4</c> — because a gradient carries the two together. The generated serializer
/// transcribes that faithfully, so raising the bit for one field makes it write the other as well.
/// </para>
///
/// <para>
/// A stored wallpaper with a second colour and no rotation therefore threw
/// <c>InvalidOperationException: Nullable object must have a value</c> while the response was being
/// written. That is past the point where a handler can fail cleanly: the caller was never answered at
/// all, and the log showed a successful <c>GetWallPapersHandler</c> followed by a serializer stack trace.
/// One seeded row (<c>gradient-rainbow</c>) took the whole of <c>account.getWallPapers</c> down.
/// </para>
/// </summary>
public class WallPaperSettingsSerializationTests
{
    [Fact]
    public void A_second_background_colour_without_a_rotation_serializes()
    {
        var settings = WallPaperSettingsHelper.PairSharedFlags(new TWallPaperSettings
        {
            BackgroundColor = 16711680,
            SecondBackgroundColor = 65280,
            ThirdBackgroundColor = 255,
            FourthBackgroundColor = 16776960
        })!;

        Should.NotThrow(() => Serialize(settings));
        settings.Rotation.ShouldBe(0);
    }

    [Fact]
    public void A_rotation_without_a_second_background_colour_serializes()
    {
        var settings = WallPaperSettingsHelper.PairSharedFlags(new TWallPaperSettings { Rotation = 45 })!;

        Should.NotThrow(() => Serialize(settings));
        settings.SecondBackgroundColor.ShouldBe(0);
    }

    /// <summary>Settings that name neither field must not gain them: the flag stays clear.</summary>
    [Fact]
    public void Settings_with_neither_field_are_left_alone()
    {
        var settings = WallPaperSettingsHelper.PairSharedFlags(new TWallPaperSettings
        {
            BackgroundColor = 16711680,
            Intensity = 50
        })!;

        settings.SecondBackgroundColor.ShouldBeNull();
        settings.Rotation.ShouldBeNull();
        Should.NotThrow(() => Serialize(settings));
    }

    [Fact]
    public void Values_that_are_present_are_kept()
    {
        var settings = WallPaperSettingsHelper.PairSharedFlags(new TWallPaperSettings
        {
            SecondBackgroundColor = 65280,
            Rotation = 90
        })!;

        settings.SecondBackgroundColor.ShouldBe(65280);
        settings.Rotation.ShouldBe(90);
    }

    [Fact]
    public void Null_settings_stay_null()
    {
        WallPaperSettingsHelper.PairSharedFlags(null).ShouldBeNull();
    }

    private static void Serialize(TWallPaperSettings settings)
    {
        var writer = new ArrayBufferWriter<byte>();
        settings.Serialize(writer);
    }
}
