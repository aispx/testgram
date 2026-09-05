using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Translation;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Translation;

/// <summary>
/// Feature: carrying styled text entities through a translation, which is what Telegram promises Premium
/// users — "correctly repositioned bold, italic, link entities for the translated message".
///
/// <para>Each entity travels as a <c>&lt;span id="N"&gt;</c> and DeepL's <c>tag_handling=html</c> moves the
/// tags to wherever the translated words ended up. The response strings here are the shape measured
/// against the live API, including that a quote inside a <c>translate="no"</c> span comes back as
/// <c>&amp;quot;</c> and that DeepL sometimes adds punctuation of its own around a span.</para>
///
/// <para>Offsets are UTF-16 code units, so an emoji outside the BMP counts as two. Getting that wrong
/// shifts every entity after it, which a client draws as formatting sliding off the words.</para>
/// </summary>
public class TranslationEntityCodecTests
{
    private static TranslationEntityCodec Codec() => new(NullLogger<TranslationEntityCodec>.Instance);

    private static TMessageEntityBold Bold(int offset, int length) => new() { Offset = offset, Length = length };

    private static TMessageEntityCode Code(int offset, int length) => new() { Offset = offset, Length = length };

    [Fact]
    public void A_text_with_no_entities_needs_no_markup()
    {
        Codec().Encode("Hello world", null).ShouldBeNull();
        Codec().Encode("Hello world", []).ShouldBeNull();
    }

    [Fact]
    public void An_entity_becomes_a_span_carrying_its_index()
    {
        // "Hello world", bold over "world"
        Codec().Encode("Hello world", [Bold(6, 5)])
            .ShouldBe("Hello <span id=\"0\">world</span>");
    }

    [Fact]
    public void Nested_entities_nest_in_the_markup()
    {
        // "This is very important news": bold over "is very important", italic over "very important"
        var text = "This is very important news";
        var encoded = Codec().Encode(text,
        [
            Bold(5, 17),
            new TMessageEntityItalic { Offset = 8, Length = 14 }
        ]);

        encoded.ShouldBe("This <span id=\"0\">is <span id=\"1\">very important</span></span> news");
    }

    /// <summary>
    /// Code is not language. Without <c>translate="no"</c> DeepL happily rewrites the identifiers inside
    /// it, and the attribute is honoured — verified against the live API.
    /// </summary>
    [Fact]
    public void Content_that_is_not_language_is_marked_untranslatable()
    {
        Codec().Encode("run print(1) now", [Code(4, 8)])
            .ShouldBe("run <span id=\"0\" translate=\"no\">print(1)</span> now");
    }

    /// <summary>
    /// A mention, a URL, a phone number and a custom emoji are all literals. A link's visible text is
    /// not, so <c>messageEntityTextUrl</c> stays translatable — the address it points at travels in the
    /// entity, not in the markup.
    /// </summary>
    [Fact]
    public void A_literal_is_opaque_but_a_link_label_is_not()
    {
        Codec().Encode("ask @durov", [new TMessageEntityMention { Offset = 4, Length = 6 }])
            .ShouldBe("ask <span id=\"0\" translate=\"no\">@durov</span>");

        Codec().Encode("click here",
                [new TMessageEntityTextUrl { Offset = 6, Length = 4, Url = "https://example.com" }])
            .ShouldBe("click <span id=\"0\">here</span>");
    }

    [Fact]
    public void Markup_characters_in_the_text_are_escaped()
    {
        Codec().Encode("a < b & c > d \"q\"", [Bold(0, 1)])
            .ShouldBe("<span id=\"0\">a</span> &lt; b &amp; c &gt; d &quot;q&quot;");
    }

    /// <summary>
    /// Entities the server never echoes back must not take an id either, or the decoder indexes the
    /// wrong entity when it maps a span back.
    /// </summary>
    [Fact]
    public void A_dropped_entity_type_is_left_out_but_does_not_shift_the_ids()
    {
        var encoded = Codec().Encode("hello world",
        [
            new TMessageEntityUnknown { Offset = 0, Length = 5 },
            Bold(6, 5)
        ]);

        encoded.ShouldBe("hello <span id=\"1\">world</span>");
    }

    [Fact]
    public void A_translated_span_comes_back_as_a_repositioned_entity()
    {
        var original = new List<IMessageEntity> { Bold(6, 5) };

        var result = Codec().Decode("Привет <span id=\"0\">мир</span>", original);

        result.Text.ShouldBe("Привет мир");
        result.Entities.Count.ShouldBe(1);
        result.Entities[0].ShouldBeOfType<TMessageEntityBold>();
        result.Entities[0].Offset.ShouldBe(7);
        result.Entities[0].Length.ShouldBe(3);
    }

    /// <summary>
    /// The original instance may be a cached read model shared with other requests, so the decode must
    /// hand back a copy — and a copy that kept every argument, not just the range.
    /// </summary>
    [Fact]
    public void The_original_entity_is_not_mutated_and_its_arguments_survive()
    {
        var url = new TMessageEntityTextUrl { Offset = 0, Length = 4, Url = "https://example.com" };
        var original = new List<IMessageEntity> { url };

        var result = Codec().Decode("<span id=\"0\">ссылка</span> тут", original);

        url.Offset.ShouldBe(0);
        url.Length.ShouldBe(4);

        var decoded = result.Entities[0].ShouldBeOfType<TMessageEntityTextUrl>();
        decoded.ShouldNotBeSameAs(url);
        decoded.Url.ShouldBe("https://example.com");
        decoded.Length.ShouldBe(6);
    }

    [Fact]
    public void Nesting_survives_the_round_trip()
    {
        var original = new List<IMessageEntity> { Bold(5, 17), new TMessageEntityItalic { Offset = 8, Length = 14 } };

        var result = Codec().Decode(
            "Это <span id=\"0\">очень <span id=\"1\">важная</span></span> новость", original);

        result.Text.ShouldBe("Это очень важная новость");
        result.Entities.Count.ShouldBe(2);
        result.Entities[0].ShouldBeOfType<TMessageEntityBold>();
        result.Entities[0].Offset.ShouldBe(4);
        result.Entities[0].Length.ShouldBe(12);
        result.Entities[1].ShouldBeOfType<TMessageEntityItalic>();
        result.Entities[1].Offset.ShouldBe(10);
        result.Entities[1].Length.ShouldBe(6);
    }

    /// <summary>Measured: DeepL escapes a quote inside a span it was told not to translate.</summary>
    [Fact]
    public void Escaped_references_in_the_answer_are_unescaped()
    {
        var original = new List<IMessageEntity> { Code(0, 11) };

        var result = Codec().Decode(
            "<span id=\"0\" translate=\"no\">print(&quot;hi&quot;)</span> &amp; then &lt;b&gt;", original);

        result.Text.ShouldBe("print(\"hi\") & then <b>");
        result.Entities[0].Offset.ShouldBe(0);
        result.Entities[0].Length.ShouldBe(11);
    }

    /// <summary>
    /// An emoji outside the BMP is two UTF-16 code units, which is the unit an entity offset is measured
    /// in. Counting characters would put the entity one unit early and a client would bold the wrong run.
    /// </summary>
    [Fact]
    public void An_astral_emoji_counts_as_two_code_units()
    {
        var result = Codec().Decode("\U0001F600 <span id=\"0\">жирный</span>", [Bold(0, 6)]);

        result.Text.ShouldBe("\U0001F600 жирный");
        result.Entities[0].Offset.ShouldBe(3);
        result.Entities[0].Length.ShouldBe(6);
    }

    /// <summary>
    /// A span the provider emptied is not an entity: a zero-length entity is invalid on the wire and
    /// clients reject the whole message over it.
    /// </summary>
    [Fact]
    public void A_span_the_translation_emptied_is_dropped()
    {
        var result = Codec().Decode("Привет <span id=\"0\"></span>мир", [Bold(6, 5)]);

        result.Text.ShouldBe("Привет мир");
        result.Entities.ShouldBeEmpty();
    }

    /// <summary>
    /// Markup that does not round-trip degrades to its visible text. A bad offset is worse than lost
    /// formatting: the client that receives it draws nothing at all, or crashes.
    /// </summary>
    [Theory]
    [InlineData("Привет <span id=\"0\">мир")]
    [InlineData("Привет <span id=\"9\">мир</span>")]
    [InlineData("Привет <span id=\"0\">мир</span")]
    public void Markup_that_cannot_be_understood_degrades_to_plain_text(string answer)
    {
        var result = Codec().Decode(answer, [Bold(6, 5)]);

        result.Entities.ShouldBeEmpty();
        result.Text.ShouldNotBeNullOrEmpty();
        result.Text.ShouldNotContain("<span");
    }

    /// <summary>A span the provider duplicated is one entity, not two.</summary>
    [Fact]
    public void A_duplicated_span_yields_one_entity()
    {
        var result = Codec().Decode(
            "<span id=\"0\">раз</span> и <span id=\"0\">два</span>", [Bold(0, 3)]);

        result.Text.ShouldBe("раз и два");
        result.Entities.Count.ShouldBe(1);
        result.Entities[0].Offset.ShouldBe(0);
    }
}
