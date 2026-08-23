using MyTelegram.Messenger.Services.Entities;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Entities;

/// <summary>
/// Covers the bounds and argument checks of
/// <a href="https://corefork.telegram.org/api/entities#entity-length">entity length »</a>.
/// </summary>
public class MessageEntityValidatorTests
{
    private static string Reject(string? text, params IMessageEntity[] entities)
    {
        return Should.Throw<RpcException>(() => MessageEntityValidator.Validate(text, entities)).RpcError.Message;
    }

    private static void Accept(string? text, params IMessageEntity[] entities)
    {
        MessageEntityValidator.Validate(text, entities);
    }

    [Fact]
    public void Negative_offset_is_rejected()
    {
        Reject("hello", new TMessageEntityBold { Offset = -1, Length = 2 })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Zero_length_is_rejected()
    {
        Reject("hello", new TMessageEntityBold { Offset = 0, Length = 0 })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Range_past_the_end_is_rejected()
    {
        Reject("hello", new TMessageEntityBold { Offset = 3, Length = 10 })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Whole_text_is_accepted()
    {
        Accept("hello", new TMessageEntityBold { Offset = 0, Length = 5 });
    }

    [Fact]
    public void A_boundary_inside_a_surrogate_pair_is_rejected()
    {
        // "😀" is a single codepoint stored as two UTF-16 code units, so length 1 cuts it in half.
        const string text = "😀";
        text.Length.ShouldBe(2);

        Reject(text, new TMessageEntityBold { Offset = 0, Length = 1 })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
        Accept(text, new TMessageEntityBold { Offset = 0, Length = 2 });
    }

    [Fact]
    public void Too_many_entities_are_rejected()
    {
        var text = new string('a', 400);
        var entities = Enumerable.Range(0, MessageEntityValidator.MaxEntities + 1)
            .Select(i => (IMessageEntity)new TMessageEntityBold { Offset = i, Length = 1 })
            .ToArray();

        Reject(text, entities).ShouldContain("ENTITIES_TOO_LONG");
    }

    [Fact]
    public void The_entity_cap_itself_is_accepted()
    {
        var text = new string('a', 200);
        var entities = Enumerable.Range(0, MessageEntityValidator.MaxEntities)
            .Select(i => (IMessageEntity)new TMessageEntityBold { Offset = i, Length = 1 })
            .ToArray();

        Accept(text, entities);
    }

    [Fact]
    public void An_empty_text_rejects_any_entity()
    {
        Reject(string.Empty, new TMessageEntityBold { Offset = 0, Length = 1 })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Text_url_scheme_is_checked()
    {
        Accept("click", new TMessageEntityTextUrl { Offset = 0, Length = 5, Url = "https://example.com" });
        Accept("click", new TMessageEntityTextUrl { Offset = 0, Length = 5, Url = "tg://user?id=1" });
        Accept("click", new TMessageEntityTextUrl { Offset = 0, Length = 5, Url = "example.com" });

        Reject("click", new TMessageEntityTextUrl { Offset = 0, Length = 5, Url = "javascript:alert(1)" })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
        Reject("click", new TMessageEntityTextUrl { Offset = 0, Length = 5, Url = string.Empty })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Pre_language_must_be_a_code()
    {
        Accept("code", new TMessageEntityPre { Offset = 0, Length = 4, Language = "c++" });
        Accept("code", new TMessageEntityPre { Offset = 0, Length = 4, Language = string.Empty });

        Reject("code", new TMessageEntityPre { Offset = 0, Length = 4, Language = "not a language" })
            .ShouldContain("ENTITY_BOUNDS_INVALID");
    }

    [Fact]
    public void Mention_name_needs_a_real_user_id()
    {
        Reject("name", new TMessageEntityMentionName { Offset = 0, Length = 4, UserId = 0 })
            .ShouldContain("ENTITY_MENTION_USER_INVALID");
    }

    [Fact]
    public void Unknown_entities_are_ignored_rather_than_rejected()
    {
        // messageEntityUnknown cannot be sent as input, so an out-of-range copy is dropped by the
        // normaliser instead of failing the whole request.
        Accept("hello", new TMessageEntityUnknown { Offset = 40, Length = 90 });
    }
}
