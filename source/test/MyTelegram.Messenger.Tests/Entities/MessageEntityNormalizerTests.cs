using MyTelegram.Messenger.Services.Entities;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Entities;

/// <summary>
/// Covers the nesting rules of <a href="https://corefork.telegram.org/api/entities">styled text »</a>,
/// as implemented by tdlib's <c>fix_entities</c>.
/// </summary>
public class MessageEntityNormalizerTests
{
    private const string Text = "0123456789abcdefghij";

    private static List<IMessageEntity> Normalize(params IMessageEntity[] entities)
    {
        return MessageEntityNormalizer.Normalize(Text, entities);
    }

    [Fact]
    public void Entities_come_back_sorted_by_offset()
    {
        var result = Normalize(
            new TMessageEntityBold { Offset = 10, Length = 4 },
            new TMessageEntityItalic { Offset = 2, Length = 4 });

        result.Select(p => p.Offset).ShouldBe([2, 10]);
    }

    [Fact]
    public void Exact_duplicates_are_dropped()
    {
        // This is what stops an edit round trip from doubling the entity list: clients send back the
        // entities they were given.
        var result = Normalize(
            new TMessageEntityBold { Offset = 0, Length = 4 },
            new TMessageEntityBold { Offset = 0, Length = 4 });

        result.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_text_url_duplicate_with_a_different_url_is_kept_apart_from_the_first()
    {
        var result = Normalize(
            new TMessageEntityTextUrl { Offset = 0, Length = 4, Url = "https://a.example" },
            new TMessageEntityTextUrl { Offset = 0, Length = 4, Url = "https://b.example" });

        // Same range, different target: the second is not a duplicate, but it cannot be nested in the
        // first continuous entity either, so only one survives.
        result.ShouldHaveSingleItem();
        result[0].ShouldBeOfType<TMessageEntityTextUrl>().Url.ShouldBe("https://a.example");
    }

    [Fact]
    public void Out_of_range_entities_are_dropped()
    {
        Normalize(new TMessageEntityBold { Offset = 100, Length = 4 }).ShouldBeEmpty();
        Normalize(new TMessageEntityBold { Offset = 0, Length = 0 }).ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_and_diff_entities_are_dropped()
    {
        Normalize(
            new TMessageEntityUnknown { Offset = 0, Length = 4 },
            new TMessageEntityDiffInsert { Offset = 0, Length = 4 },
            new TMessageEntityDiffDelete { Offset = 0, Length = 4 }).ShouldBeEmpty();
    }

    [Fact]
    public void Bold_may_contain_italic()
    {
        var result = Normalize(
            new TMessageEntityBold { Offset = 0, Length = 10 },
            new TMessageEntityItalic { Offset = 2, Length = 4 });

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Pre_cannot_contain_anything()
    {
        var result = Normalize(
            new TMessageEntityPre { Offset = 0, Length = 10, Language = string.Empty },
            new TMessageEntityBold { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityPre>();
    }

    [Fact]
    public void Code_cannot_contain_anything()
    {
        var result = Normalize(
            new TMessageEntityCode { Offset = 0, Length = 10 },
            new TMessageEntityItalic { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityCode>();
    }

    [Fact]
    public void Pre_cannot_be_nested_inside_bold()
    {
        var result = Normalize(
            new TMessageEntityBold { Offset = 0, Length = 10 },
            new TMessageEntityPre { Offset = 2, Length = 4, Language = string.Empty });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityBold>();
    }

    [Fact]
    public void Pre_may_be_nested_inside_a_blockquote()
    {
        var result = Normalize(
            new TMessageEntityBlockquote { Offset = 0, Length = 10 },
            new TMessageEntityPre { Offset = 2, Length = 4, Language = string.Empty });

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Blockquotes_cannot_be_nested()
    {
        var result = Normalize(
            new TMessageEntityBlockquote { Offset = 0, Length = 10 },
            new TMessageEntityBlockquote { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().Length.ShouldBe(10);
    }

    [Fact]
    public void A_continuous_entity_cannot_contain_another_continuous_entity()
    {
        var result = Normalize(
            new TMessageEntityTextUrl { Offset = 0, Length = 10, Url = "https://example.com" },
            new TMessageEntityMention { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityTextUrl>();
    }

    [Fact]
    public void A_continuous_entity_may_contain_bold()
    {
        var result = Normalize(
            new TMessageEntityTextUrl { Offset = 0, Length = 10, Url = "https://example.com" },
            new TMessageEntityBold { Offset = 2, Length = 4 });

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void A_blockquote_cannot_sit_inside_a_continuous_entity()
    {
        var result = Normalize(
            new TMessageEntityTextUrl { Offset = 0, Length = 10, Url = "https://example.com" },
            new TMessageEntityBlockquote { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityTextUrl>();
    }

    [Fact]
    public void A_splittable_entity_crossing_a_container_boundary_is_split()
    {
        var result = Normalize(
            new TMessageEntityBlockquote { Offset = 0, Length = 10 },
            new TMessageEntityBold { Offset = 6, Length = 8 });

        result.Count.ShouldBe(3);
        result[0].ShouldBeOfType<TMessageEntityBlockquote>();

        var boldPieces = result.OfType<TMessageEntityBold>().OrderBy(p => p.Offset).ToList();
        boldPieces.Count.ShouldBe(2);
        boldPieces[0].Offset.ShouldBe(6);
        boldPieces[0].Length.ShouldBe(4);
        boldPieces[1].Offset.ShouldBe(10);
        boldPieces[1].Length.ShouldBe(4);
    }

    [Fact]
    public void A_continuous_entity_crossing_a_container_boundary_is_dropped()
    {
        // Splitting a link in two would produce two half-links, so the crossing entity goes instead.
        var result = Normalize(
            new TMessageEntityBlockquote { Offset = 0, Length = 10 },
            new TMessageEntityTextUrl { Offset = 6, Length = 8, Url = "https://example.com" });

        result.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityBlockquote>();
    }

    [Fact]
    public void The_same_entity_type_is_not_nested_in_itself()
    {
        var result = Normalize(
            new TMessageEntityBold { Offset = 0, Length = 10 },
            new TMessageEntityBold { Offset = 2, Length = 4 });

        result.ShouldHaveSingleItem().Length.ShouldBe(10);
    }

    [Fact]
    public void Adjacent_entities_are_left_alone()
    {
        var result = Normalize(
            new TMessageEntityBold { Offset = 0, Length = 5 },
            new TMessageEntityBold { Offset = 5, Length = 5 });

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        var once = MessageEntityNormalizer.Normalize(Text, [
            new TMessageEntityBlockquote { Offset = 0, Length = 10 },
            new TMessageEntityBold { Offset = 6, Length = 8 },
            new TMessageEntityPre { Offset = 2, Length = 3, Language = "c++" },
            new TMessageEntityMention { Offset = 14, Length = 5 }
        ]);

        var twice = MessageEntityNormalizer.Normalize(Text, once);

        twice.Count.ShouldBe(once.Count);
        for (var i = 0; i < once.Count; i++)
        {
            twice[i].ConstructorId.ShouldBe(once[i].ConstructorId);
            twice[i].Offset.ShouldBe(once[i].Offset);
            twice[i].Length.ShouldBe(once[i].Length);
        }
    }
}
