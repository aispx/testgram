using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — hashtag indexing for stories.searchPosts.
///
/// <para>
/// Captions are indexed onto <see cref="StoryDocument.Hashtags"/> at post/edit time, and
/// stories.searchPosts matches the normalized search term against that list. Extraction and
/// normalization therefore have to agree exactly, or a story is indexed under a tag nobody can search
/// for.
/// </para>
/// </summary>
public class StoryHashtagTests
{
    [Fact]
    public void Extracts_hashtags_lowercased_without_the_hash()
    {
        StoryHelper.ExtractHashtags("Hello #World and #Telegram")
            .ShouldBe(["world", "telegram"]);
    }

    [Fact]
    public void Deduplicates_repeated_hashtags_preserving_first_occurrence_order()
    {
        StoryHelper.ExtractHashtags("#a #b #A #b #c")
            .ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void Handles_non_latin_hashtags()
    {
        StoryHelper.ExtractHashtags("Привет #Москва #대한민국 #日本")
            .ShouldBe(["москва", "대한민국", "日本"]);
    }

    [Fact]
    public void Accepts_digits_and_underscores_and_stops_at_other_punctuation()
    {
        // '_' and digits are part of a tag; ',', '-' and '.' terminate it.
        StoryHelper.ExtractHashtags("#tag_1, #tag-2 #tag.3")
            .ShouldBe(["tag_1", "tag"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no hashtags here")]
    [InlineData("# ")]
    public void Returns_empty_when_there_is_nothing_to_index(string? caption)
    {
        StoryHelper.ExtractHashtags(caption).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("#Telegram", "telegram")]
    [InlineData("Telegram", "telegram")]
    [InlineData("  #Telegram  ", "telegram")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Normalization_matches_what_extraction_stores(string? input, string expected)
    {
        StoryHelper.NormalizeHashtag(input).ShouldBe(expected);
    }

    [Fact]
    public void A_search_term_finds_the_tag_that_extraction_stored()
    {
        // The round trip that stories.searchPosts depends on.
        var stored = StoryHelper.ExtractHashtags("Trip report #TravelDiary");
        var searched = StoryHelper.NormalizeHashtag("#travelDIARY");

        stored.ShouldContain(searched);
    }
}

/// <summary>
/// Feature: stories — caption entity round-tripping.
///
/// <para>
/// Entities are persisted as JSON on the story and rebuilt on read. Earlier code wrote the constructor
/// id but matched it against ids from a different layer, so every entity came back as
/// <c>messageEntityUnknown</c> and styled captions silently lost their formatting.
/// </para>
/// </summary>
public class StoryEntitySerializationTests
{
    [Fact]
    public void Round_trips_styling_entities()
    {
        var entities = new List<IMessageEntity>
        {
            new TMessageEntityBold { Offset = 0, Length = 5 },
            new TMessageEntityItalic { Offset = 6, Length = 4 },
            new TMessageEntitySpoiler { Offset = 11, Length = 3 }
        };

        var parsed = RoundTrip(entities);

        parsed.Count.ShouldBe(3);
        parsed[0].ShouldBeOfType<TMessageEntityBold>();
        parsed[1].ShouldBeOfType<TMessageEntityItalic>();
        parsed[2].ShouldBeOfType<TMessageEntitySpoiler>();
        parsed[0].Offset.ShouldBe(0);
        parsed[0].Length.ShouldBe(5);
    }

    [Fact]
    public void Round_trips_a_text_url_with_its_url()
    {
        var parsed = RoundTrip([new TMessageEntityTextUrl { Offset = 2, Length = 7, Url = "https://t.me" }]);

        var entity = parsed[0].ShouldBeOfType<TMessageEntityTextUrl>();
        entity.Url.ShouldBe("https://t.me");
        entity.Offset.ShouldBe(2);
        entity.Length.ShouldBe(7);
    }

    [Fact]
    public void Round_trips_a_mention_name_with_its_user_id()
    {
        var parsed = RoundTrip([new TMessageEntityMentionName { Offset = 0, Length = 4, UserId = 4242 }]);

        parsed[0].ShouldBeOfType<TMessageEntityMentionName>().UserId.ShouldBe(4242);
    }

    [Fact]
    public void Round_trips_a_custom_emoji_with_its_document_id()
    {
        var parsed = RoundTrip([new TMessageEntityCustomEmoji { Offset = 1, Length = 2, DocumentId = 777 }]);

        parsed[0].ShouldBeOfType<TMessageEntityCustomEmoji>().DocumentId.ShouldBe(777);
    }

    [Fact]
    public void Round_trips_a_pre_block_with_its_language()
    {
        var parsed = RoundTrip([new TMessageEntityPre { Offset = 0, Length = 9, Language = "csharp" }]);

        parsed[0].ShouldBeOfType<TMessageEntityPre>().Language.ShouldBe("csharp");
    }

    [Fact]
    public void Serializing_nothing_yields_nothing()
    {
        StoryHelper.SerializeEntities(null).ShouldBeNull();
        StoryHelper.SerializeEntities([]).ShouldBeNull();
        StoryHelper.ParseEntities(null).ShouldBeNull();
        StoryHelper.ParseEntities("").ShouldBeNull();
    }

    [Fact]
    public void Malformed_json_is_ignored_rather_than_throwing()
    {
        // A story whose stored entities got corrupted must still be readable.
        StoryHelper.ParseEntities("{not json").ShouldBeNull();
    }

    private static List<IMessageEntity> RoundTrip(List<IMessageEntity> entities)
    {
        var json = StoryHelper.SerializeEntities(entities);
        json.ShouldNotBeNull();

        var parsed = StoryHelper.ParseEntities(json);
        parsed.ShouldNotBeNull();

        return parsed.ToList();
    }
}
