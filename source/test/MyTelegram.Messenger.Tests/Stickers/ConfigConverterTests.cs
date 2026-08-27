using Moq;
using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram.Core;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: the <c>preload_featured_stickers</c> flag of <c>help.getConfig</c>.
///
/// <para>
/// The flag asks a client to eagerly load the full sets behind the trending lists. Telegram Android
/// implements the custom-emoji half of that by calling
/// <c>MediaDataController.loadStickers(TYPE_FEATURED_EMOJIPACKS)</c>, and that constant is <c>6</c> while
/// the arrays it indexes — <c>stickerSets</c> and <c>stickersByIds</c> — are built with six elements. So a
/// non-empty <c>messages.getFeaturedEmojiStickers</c> combined with this flag throws
/// <c>ArrayIndexOutOfBoundsException: length=6; index=6</c> on the UI thread while the app is starting,
/// which is a crash loop rather than a degraded screen. Real Telegram serves the flag unset (measured
/// against the live service), and the arrays are the same size upstream, so nothing else can be the
/// intended behaviour.
/// </para>
/// </summary>
public class ConfigConverterTests
{
    private static IConfig BuildConfig()
    {
        // The mapper is only reached for dc options, and there are none in this configuration.
        var converter = new ConfigConverter(new Mock<IObjectMapper>().Object);

        return converter.ToConfig([], thisDcId: 1, mediaDcId: 1);
    }

    [Fact]
    public void Preload_featured_stickers_is_not_advertised()
    {
        BuildConfig().PreloadFeaturedStickers.ShouldBeFalse();
    }

    [Fact]
    public void The_flag_bit_stays_clear_on_the_wire()
    {
        var config = (TConfig)BuildConfig();

        config.ComputeFlag();

        // Bit 4 of config.flags is preload_featured_stickers; a client only follows the crashing path when it
        // is set, so this is the assertion that actually protects the client.
        config.Flags.IsBitSet(4).ShouldBeFalse();
    }
}
