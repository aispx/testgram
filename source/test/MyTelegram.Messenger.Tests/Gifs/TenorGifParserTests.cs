using MyTelegram.Messenger.Services.Gifs;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: mapping a Tenor search payload into inline GIF results, for the <c>@gif</c> bot behind
/// <a href="https://corefork.telegram.org/api/gifs#searching-gifs">GIF search</a>.
///
/// <para>
/// The shape of the payload is Tenor's, not Telegram's, so the mapping is where a provider change would
/// break. It runs against a canned response rather than the live API: the tests must not depend on the
/// network, and a result without an MPEG4 rendition has to be dropped rather than emitted half-built —
/// the MPEG4 is the file that actually gets sent.
/// </para>
/// </summary>
public class TenorGifParserTests
{
    /// <summary>Trimmed from a real <c>/v2/search?q=crying%20dog</c> response.</summary>
    private const string SearchPayload = """
        {
          "results": [
            {
              "id": "4138822033792608878",
              "content_description": "a dog is crying while looking at an iphone",
              "media_formats": {
                "tinygif": {
                  "url": "https://media.tenor.com/OXAMthjEqm4AAAAM/dog-scroll.gif",
                  "dims": [165, 165],
                  "size": 669376,
                  "duration": 4.8
                },
                "nanomp4": {
                  "url": "https://media.tenor.com/OXAMthjEqm4AAAP2/dog-scroll.mp4",
                  "dims": [150, 150],
                  "size": 26214,
                  "duration": 4.8
                },
                "tinymp4": {
                  "url": "https://media.tenor.com/OXAMthjEqm4AAAP1/dog-scroll.mp4",
                  "dims": [320, 320],
                  "size": 74251,
                  "duration": 4.8
                },
                "tinygifpreview": {
                  "url": "https://media.tenor.com/OXAMthjEqm4AAAAN/dog-scroll.png",
                  "dims": [220, 220],
                  "size": 31108,
                  "duration": 0
                },
                "mp4": {
                  "url": "https://media.tenor.com/OXAMthjEqm4AAAPo/dog-scroll.mp4",
                  "dims": [498, 498],
                  "size": 163575,
                  "duration": 4.8
                }
              }
            }
          ],
          "next": "CAMQgNyT_7e3lgMaHg"
        }
        """;

    [Fact]
    public void A_result_keeps_the_mpeg4_the_preview_and_the_dimensions()
    {
        var result = TenorGifParser.Parse(SearchPayload);

        var gif = result.Gifs.ShouldHaveSingleItem();
        gif.Id.ShouldBe("4138822033792608878");
        gif.Description.ShouldBe("a dog is crying while looking at an iphone");
        gif.Mp4Url.ShouldBe("https://media.tenor.com/OXAMthjEqm4AAAPo/dog-scroll.mp4");
        gif.Mp4Size.ShouldBe(163575);
        gif.Width.ShouldBe(498);
        gif.Height.ShouldBe(498);
        // Rounded up: a duration of 0 would make the client treat it as a still.
        gif.DurationSeconds.ShouldBe(5);
    }

    /// <summary>
    /// The preview is what a grid of thirty tiles downloads, so the smallest playable rendition wins.
    /// The animated <c>tinygif</c> in the same payload is 669 KB — four times the MPEG4 it previews —
    /// and picking it is what makes GIF search feel broken on a phone.
    /// </summary>
    [Fact]
    public void The_preview_is_the_smallest_mpeg4_rendition()
    {
        var gif = TenorGifParser.Parse(SearchPayload).Gifs.ShouldHaveSingleItem();

        gif.ThumbUrl.ShouldBe("https://media.tenor.com/OXAMthjEqm4AAAP2/dog-scroll.mp4");
        gif.ThumbSize.ShouldBe(26214);
        gif.ThumbMimeType.ShouldBe("video/mp4");
        gif.ThumbWidth.ShouldBe(150);
        gif.ThumbHeight.ShouldBe(150);
    }

    [Fact]
    public void A_larger_mpeg4_preview_is_used_when_the_smallest_is_missing()
    {
        var payload = """
            {
              "results": [
                {
                  "id": "1",
                  "media_formats": {
                    "tinymp4": { "url": "https://media.tenor.com/x/tiny.mp4", "dims": [320, 240], "size": 40, "duration": 2 },
                    "mp4": { "url": "https://media.tenor.com/x/y.mp4", "dims": [498, 374], "size": 90, "duration": 2 }
                  }
                }
              ]
            }
            """;

        var gif = TenorGifParser.Parse(payload).Gifs.ShouldHaveSingleItem();
        gif.ThumbUrl.ShouldBe("https://media.tenor.com/x/tiny.mp4");
        gif.ThumbMimeType.ShouldBe("video/mp4");
    }

    /// <summary>
    /// With no MPEG4 rendition to preview, the still image is the fallback — a client that cannot
    /// animate a thumbnail then still has something to draw.
    /// </summary>
    [Fact]
    public void A_still_image_is_the_fallback_preview()
    {
        var payload = """
            {
              "results": [
                {
                  "id": "1",
                  "media_formats": {
                    "tinygifpreview": { "url": "https://media.tenor.com/x/p.png", "dims": [220, 165], "size": 31, "duration": 0 },
                    "mp4": { "url": "https://media.tenor.com/x/y.mp4", "dims": [498, 374], "size": 90, "duration": 2 }
                  }
                }
              ]
            }
            """;

        var gif = TenorGifParser.Parse(payload).Gifs.ShouldHaveSingleItem();
        gif.ThumbUrl.ShouldBe("https://media.tenor.com/x/p.png");
        gif.ThumbMimeType.ShouldBe("image/png");
    }

    /// <summary>
    /// The renditions the parser reads are the ones the request asks for: a format missing from
    /// <c>media_filter</c> is simply absent from the response.
    /// </summary>
    [Fact]
    public void Every_rendition_the_parser_reads_is_requested()
    {
        var requested = TenorGifParser.MediaFilter.Split(',');

        requested.ShouldContain("mp4");
        requested.ShouldContain("nanomp4");
        requested.ShouldContain("tinymp4");
        requested.ShouldContain("tinygifpreview");
        // The animated preview is deliberately not requested: it is bigger than the MPEG4.
        requested.ShouldNotContain("tinygif");
    }

    [Fact]
    public void The_cursor_is_carried_across_as_the_next_offset()
    {
        TenorGifParser.Parse(SearchPayload).NextPosition.ShouldBe("CAMQgNyT_7e3lgMaHg");
    }

    [Fact]
    public void An_empty_cursor_is_reported_as_no_more_pages()
    {
        var payload = """{ "results": [], "next": "" }""";

        TenorGifParser.Parse(payload).NextPosition.ShouldBeNull();
    }

    [Fact]
    public void A_result_without_an_mpeg4_is_skipped()
    {
        var payload = """
            {
              "results": [
                {
                  "id": "1",
                  "media_formats": {
                    "tinygif": { "url": "https://media.tenor.com/x/y.gif", "dims": [1, 1], "size": 1, "duration": 1 }
                  }
                }
              ]
            }
            """;

        TenorGifParser.Parse(payload).Gifs.ShouldBeEmpty();
    }

    [Fact]
    public void A_result_without_a_preview_is_still_usable()
    {
        var payload = """
            {
              "results": [
                {
                  "id": "1",
                  "media_formats": {
                    "mp4": { "url": "https://media.tenor.com/x/y.mp4", "dims": [2, 2], "size": 3, "duration": 1 }
                  }
                }
              ]
            }
            """;

        var gif = TenorGifParser.Parse(payload).Gifs.ShouldHaveSingleItem();
        gif.ThumbUrl.ShouldBeNull();
        gif.ThumbSize.ShouldBe(0);
    }
    [Fact]
    public void Nothing_usable_comes_back_from_nothing()
    {
        TenorGifParser.Parse(null).Gifs.ShouldBeEmpty();
        TenorGifParser.Parse("").Gifs.ShouldBeEmpty();
        // A truncated body must degrade to "no results" rather than throwing inside an inline query.
        TenorGifParser.Parse("{ \"results\": [").Gifs.ShouldBeEmpty();
    }
}
