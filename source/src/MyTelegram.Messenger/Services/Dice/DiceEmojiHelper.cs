namespace MyTelegram.Messenger.Services.Dice;

/// <summary>
/// One <a href="https://corefork.telegram.org/api/dice">dice</a> emoji: which sticker set animates it,
/// how many outcomes it has, and which outcome counts as a win.
/// </summary>
/// <param name="Emoticon">
/// The emoji exactly as it is advertised in <c>emojies_send_dice</c>. The soccer ball appears twice, with
/// and without U+FE0F, because that is what the real service publishes and what TDLib's own default list
/// (<c>StickersManager.cpp</c>, <c>dice_emojis</c>) contains; clients strip the variation selector before
/// keying, so both forms have to resolve.
/// </param>
/// <param name="ShortName">Telegram's own short name of the set, so a mirrored catalogue needs no aliasing.</param>
/// <param name="MaxValue">
/// The highest value the server may roll. Clients do not check this: TDLib's <c>MessageDice::is_valid()</c>
/// allows up to 6 for the die and the dart and up to 1000 for everything else, and a value past the end of
/// the set's document list simply draws nothing. So the range is the server's to get right, and it has to
/// stay equal to <c>documents.Count - 1</c> of the seeded set (the first document is the idle preview).
/// </param>
/// <param name="SuccessValue">
/// The winning value, or <c>null</c> when the emoji has no win state (the plain die). Mirrors
/// <c>emojies_send_dice_success</c>.
/// </param>
/// <param name="SuccessFrameStart">The frame at which the client plays the fireworks, or <c>null</c>.</param>
public readonly record struct DiceEmoji(
    string Emoticon,
    string ShortName,
    int MaxValue,
    int? SuccessValue,
    int? SuccessFrameStart);

/// <summary>
/// The single source of truth for <a href="https://corefork.telegram.org/api/dice">animated dice</a>: the
/// supported emoji, their sticker sets and their outcome ranges. Sending, sticker set resolution and the
/// <c>emojies_send_dice</c>/<c>emojies_send_dice_success</c> app config fields all have to agree — an
/// emoji advertised in the config but unknown here is a dice a client offers and the server refuses, and
/// an emoji known here but absent from the config is one no client will ever send.
/// </summary>
public static class DiceEmojiHelper
{
    /// <summary>
    /// In the order they are advertised in <c>emojies_send_dice</c>. The order is part of the contract:
    /// TDLib builds its <c>dice_emojis</c> option straight from that vector, and clients cache it.
    /// </summary>
    public static readonly IReadOnlyList<DiceEmoji> All =
    [
        new("🎲", "AnimatedDice2", 6, null, null),
        new("🎯", "AnimatedDart", 6, 6, 62),
        new("🏀", "AnimatedBasketball", 5, 5, 110),
        new("⚽", "AnimatedPenalty", 5, 5, 110),
        new("⚽️", "AnimatedPenalty", 5, 5, 110),
        new("🎰", "SlotMachineAnimated", 64, 64, 110),
        new("🎳", "AnimatedBowling", 6, 6, 110)
    ];

    /// <summary>
    /// The slot machine is the one irregular set: its value is a 6-bit field decoded into a background, a
    /// lever and three reels, so it carries a fixed 21 documents instead of one per outcome. TDLib refuses
    /// to draw it below that count (<c>StickersManager::get_dice_stickers</c>).
    /// </summary>
    public const string SlotMachineEmoticon = "🎰";

    /// <summary>Documents the slot machine set must hold.</summary>
    public const int SlotMachineDocumentCount = 21;

    private static readonly Dictionary<string, DiceEmoji> ByEmoticon =
        All.ToDictionary(p => p.Emoticon, StringComparer.Ordinal);

    /// <summary>
    /// Whether this is a dice emoji at all. Comparison is <see cref="StringComparer.Ordinal"/> on purpose:
    /// the two soccer ball forms are separate entries rather than one normalised key, so what the server
    /// accepts is exactly what it advertised.
    /// </summary>
    public static bool TryGet(string? emoticon, out DiceEmoji dice)
    {
        if (string.IsNullOrEmpty(emoticon))
        {
            dice = default;

            return false;
        }

        return ByEmoticon.TryGetValue(emoticon, out dice);
    }

    /// <summary>The set backing this emoji, or <c>null</c> when it is not a dice emoji.</summary>
    public static string? GetShortName(string? emoticon)
    {
        return TryGet(emoticon, out var dice) ? dice.ShortName : null;
    }

    /// <summary>How many documents the set for this emoji must hold: the idle preview plus one per outcome.</summary>
    public static int GetDocumentCount(DiceEmoji dice)
    {
        return dice.Emoticon == SlotMachineEmoticon ? SlotMachineDocumentCount : dice.MaxValue + 1;
    }

    /// <summary>
    /// Rolls an outcome. The value belongs to the server alone — <c>inputMediaDice</c> carries nothing but
    /// the emoji — and an emoji the server never advertised is <c>EMOTICON_INVALID</c> rather than a roll on
    /// a set that does not exist.
    /// </summary>
    /// <remarks>
    /// Values start at 1: 0 is the client's "not rolled yet" sentinel (td_api documents
    /// <c>messageDice.value</c> as "If the value is 0, then the dice don't have final state yet") and must
    /// never leave the server. The upper bound comes from the table because no client bounds it — TDLib's
    /// <c>MessageDice::is_valid()</c> accepts up to 1000 for everything but the die and the dart, then draws
    /// nothing when the value runs past the sticker set.
    /// </remarks>
    public static int Roll(string? emoticon)
    {
        if (!TryGet(emoticon, out var dice))
        {
            RpcErrors.RpcErrors400.EmoticonInvalid.ThrowRpcError();
        }

        return Random.Shared.Next(1, dice.MaxValue + 1);
    }
}
