using System.Reflection;
using MyTelegram.Messenger.Services.Gifs;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: the inline result the built-in <c>@gif</c> bot returns for a Tenor hit
/// (<a href="https://corefork.telegram.org/api/gifs#searching-gifs">GIF search</a>).
///
/// <para>
/// What ends up in <c>thumb</c> decides how much a client downloads to draw the grid, and clients are
/// specific about it: Android uses <c>thumb</c> instead of <c>content</c> for a <c>gif</c> result exactly
/// when the thumb's mime type is <c>video/mp4</c> (<c>ContextLinkCell</c>), and tdlib treats a
/// <c>video/mp4</c> thumbnail as an animation (<c>get_web_document_photo_size</c>). Both then play a
/// preview measured in tens of kilobytes instead of the full rendition.
/// </para>
/// </summary>
public class GifSearchResultTests
{
    private static readonly TenorGif Gif = new(
        Id: "42",
        Description: "a cat",
        Mp4Url: "https://media.tenor.com/x/full.mp4",
        Mp4Size: 163575,
        Width: 498,
        Height: 374,
        DurationSeconds: 5,
        ThumbUrl: "https://media.tenor.com/x/nano.mp4",
        ThumbSize: 26214,
        ThumbMimeType: "video/mp4",
        ThumbWidth: 150,
        ThumbHeight: 113);

    [Fact]
    public void The_grid_preview_is_the_small_mpeg4_and_the_full_one_is_only_referenced()
    {
        var result = Build(Gif).ShouldBeOfType<TInputBotInlineResult>();

        result.Type.ShouldBe("gif");
        result.Id.ShouldBe("42");

        var content = result.Content.ShouldBeOfType<TInputWebDocument>();
        content.Url.ShouldBe("https://media.tenor.com/x/full.mp4");
        content.MimeType.ShouldBe("video/mp4");

        var thumb = result.Thumb.ShouldBeOfType<TInputWebDocument>();
        thumb.Url.ShouldBe("https://media.tenor.com/x/nano.mp4");
        thumb.MimeType.ShouldBe("video/mp4");
        thumb.Size.ShouldBeLessThan(content.Size);
    }

    /// <summary>
    /// Clients size the tile from the preview's own dimensions, so an MPEG4 preview has to carry them —
    /// and has to be marked animated, or it is treated as a plain video and not played in the grid.
    /// </summary>
    [Fact]
    public void An_mpeg4_preview_carries_its_own_dimensions_and_the_animated_marker()
    {
        var thumb = Build(Gif).ShouldBeOfType<TInputBotInlineResult>().Thumb.ShouldBeOfType<TInputWebDocument>();

        thumb.Attributes.OfType<TDocumentAttributeAnimated>().ShouldHaveSingleItem();
        var video = thumb.Attributes.OfType<TDocumentAttributeVideo>().ShouldHaveSingleItem();
        video.W.ShouldBe(150);
        video.H.ShouldBe(113);
        video.Nosound.ShouldBeTrue();
    }

    [Fact]
    public void A_still_preview_carries_an_image_size_instead()
    {
        var gif = Gif with { ThumbUrl = "https://media.tenor.com/x/p.png", ThumbMimeType = "image/png" };

        var thumb = Build(gif).ShouldBeOfType<TInputBotInlineResult>().Thumb.ShouldBeOfType<TInputWebDocument>();

        thumb.Attributes.OfType<TDocumentAttributeVideo>().ShouldBeEmpty();
        var size = thumb.Attributes.OfType<TDocumentAttributeImageSize>().ShouldHaveSingleItem();
        size.W.ShouldBe(150);
        size.H.ShouldBe(113);
    }

    /// <summary>A preview Tenor did not report leaves the result usable, drawn from the content instead.</summary>
    [Fact]
    public void A_result_without_a_preview_has_no_thumb()
    {
        var gif = Gif with { ThumbUrl = null };

        Build(gif).ShouldBeOfType<TInputBotInlineResult>().Thumb.ShouldBeNull();
    }

    /// <summary>
    /// <c>BuildTenorResult</c> is a private detail of the bot service, but what it produces is the wire
    /// contract with every client, so it is exercised directly rather than through a mocked Tenor call.
    /// </summary>
    private static IInputBotInlineResult Build(TenorGif gif)
    {
        var method = typeof(GifSearchBotService).GetMethod("BuildTenorResult",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (IInputBotInlineResult)method.Invoke(null, [gif])!;
    }
}
