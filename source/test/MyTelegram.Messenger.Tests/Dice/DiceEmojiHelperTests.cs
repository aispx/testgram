using MyTelegram.Messenger.Services.Dice;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Dice;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/dice">animated dice</a> — the emoji the server
/// accepts, the sets that animate them and the outcome each roll may produce.
///
/// <para>
/// The value is the server's alone: <c>inputMediaDice</c> carries nothing but an emoji, and no client
/// bounds what comes back. TDLib's <c>MessageDice::is_valid()</c> lets 1..6 through for the die and the
/// dart and 1..1000 through for everything else, and a value past the end of the sticker set simply draws
/// nothing — no error, no log line. So the range table here is the only thing standing between a roll and
/// an empty bubble, and it has to agree with both the sets that are seeded and the
/// <c>emojies_send_dice</c> list the server advertised.
/// </para>
/// </summary>
public class DiceEmojiHelperTests
{
    private static readonly string[] ExpectedEmoticons = ["🎲", "🎯", "🏀", "⚽", "⚽️", "🎰", "🎳"];

    private static TJsonObject Config()
    {
        return (TJsonObject)new AppConfigHelper().GetAppConfig();
    }

    private static IJSONValue? ConfigValue(string key)
    {
        return Config().Value
            .OfType<TJsonObjectValue>()
            .FirstOrDefault(p => p.Key == key)
            ?.Value;
    }

    /// <summary>
    /// The order is part of the contract, not presentation: TDLib turns the vector straight into its
    /// <c>dice_emojis</c> option and indexes <c>emojies_send_dice_success</c> against it.
    /// </summary>
    [Fact]
    public void Table_matches_the_advertised_emojies_send_dice_list()
    {
        var advertised = ((TJsonArray)ConfigValue("emojies_send_dice")!).Value
            .OfType<TJsonString>()
            .Select(p => p.Value)
            .ToList();

        advertised.ShouldBe(ExpectedEmoticons);
        DiceEmojiHelper.All.Select(p => p.Emoticon).ShouldBe(advertised);
    }

    /// <summary>
    /// A win state the config claims but the table does not know (or the reverse) means the client shows
    /// fireworks on a value the server never rolls, or never shows them at all.
    /// </summary>
    [Fact]
    public void Table_matches_the_advertised_emojies_send_dice_success_map()
    {
        var advertised = ((TJsonObject)ConfigValue("emojies_send_dice_success")!).Value
            .OfType<TJsonObjectValue>()
            .ToDictionary(p => p.Key, p => (TJsonObject)p.Value);

        foreach (var dice in DiceEmojiHelper.All)
        {
            if (dice.SuccessValue == null)
            {
                // The plain die has no win state, and Telegram omits it from the map for exactly that
                // reason: TDLib stores the absence as "0" and then never reports a success frame.
                advertised.ShouldNotContainKey(dice.Emoticon);

                continue;
            }

            advertised.ShouldContainKey(dice.Emoticon);

            var entry = advertised[dice.Emoticon].Value.OfType<TJsonObjectValue>().ToList();
            Number(entry, "value").ShouldBe(dice.SuccessValue!.Value);
            Number(entry, "frame_start").ShouldBe(dice.SuccessFrameStart!.Value);
        }

        // Nothing in the map may be missing from the table either.
        advertised.Keys.ShouldBeSubsetOf(DiceEmojiHelper.All.Select(p => p.Emoticon));

        static int Number(List<TJsonObjectValue> entry, string key)
        {
            return (int)((TJsonNumber)entry.Single(p => p.Key == key).Value).Value;
        }
    }

    /// <summary>
    /// A win the server cannot roll is a win nobody ever sees, so the success value has to sit inside the
    /// range.
    /// </summary>
    [Fact]
    public void Every_win_value_is_reachable()
    {
        foreach (var dice in DiceEmojiHelper.All.Where(p => p.SuccessValue != null))
        {
            dice.SuccessValue!.Value.ShouldBeInRange(1, dice.MaxValue);
        }
    }

    [Fact]
    public void Rolls_stay_inside_the_range_and_never_produce_the_not_rolled_sentinel()
    {
        foreach (var dice in DiceEmojiHelper.All)
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < 20_000; i++)
            {
                seen.Add(DiceEmojiHelper.Roll(dice.Emoticon));
            }

            // 0 is td_api's "the dice don't have final state yet"; sending it would freeze the animation
            // on the idle frame forever.
            seen.ShouldNotContain(0);
            seen.Min().ShouldBe(1);
            seen.Max().ShouldBe(dice.MaxValue);
        }
    }

    /// <summary>
    /// Both soccer ball forms have to work. The service advertises the emoji with and without U+FE0F (so
    /// does TDLib's own default <c>dice_emojis</c>), clients strip the variation selector before keying, and
    /// they both animate the same pack.
    /// </summary>
    [Fact]
    public void Both_soccer_ball_forms_resolve_to_the_same_set()
    {
        DiceEmojiHelper.GetShortName("⚽").ShouldBe("AnimatedPenalty");
        DiceEmojiHelper.GetShortName("⚽️").ShouldBe("AnimatedPenalty");
    }

    [Theory]
    [InlineData("🍆")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void An_emoji_the_server_never_advertised_is_refused(string? emoticon)
    {
        // Rolling anyway would answer with a messageMediaDice whose sticker set no client can resolve:
        // a permanently blank bubble that logs nothing on either side.
        var error = Should.Throw<RpcException>(() => DiceEmojiHelper.Roll(emoticon));

        error.RpcError.Message.ShouldBe("EMOTICON_INVALID");
        DiceEmojiHelper.TryGet(emoticon, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_slot_machine_needs_its_own_fixed_document_count()
    {
        // Its value is a 6-bit field decoded into background, lever and three reels rather than one
        // animation per outcome, and TDLib refuses to draw a set holding fewer than 21 documents.
        DiceEmojiHelper.TryGet(DiceEmojiHelper.SlotMachineEmoticon, out var slot).ShouldBeTrue();
        DiceEmojiHelper.GetDocumentCount(slot).ShouldBe(21);

        foreach (var dice in DiceEmojiHelper.All.Where(p => p.Emoticon != DiceEmojiHelper.SlotMachineEmoticon))
        {
            // Everything else is the idle preview plus one animation per outcome.
            DiceEmojiHelper.GetDocumentCount(dice).ShouldBe(dice.MaxValue + 1);
        }
    }

    /// <summary>
    /// The slot machine's 1..64 space has to decode to real documents. This is TDLib's own arithmetic from
    /// <c>StickersManager::get_dice_stickers</c>, and Android's <c>SlotsDrawable</c> reads the same value as
    /// a bitfield; if the server rolled outside the space the client would index past the set.
    /// </summary>
    [Fact]
    public void Every_slot_machine_value_decodes_to_documents_inside_the_set()
    {
        for (var value = 1; value <= 64; value++)
        {
            var background = value is 1 or 22 or 43 or 64 ? 1 : 0;
            const int lever = 2;
            var left = value == 64 ? 3 : 8;
            var center = value == 64 ? 9 : 14;
            var right = value == 64 ? 15 : 20;

            if (value != 64)
            {
                left = 4 + value % 4;
                center = 10 + (value + 3) / 4 % 4;
                right = 16 + (value + 15) / 16 % 4;
            }

            foreach (var index in new[] { background, lever, left, center, right })
            {
                index.ShouldBeInRange(0, DiceEmojiHelper.SlotMachineDocumentCount - 1);
            }
        }
    }
}
