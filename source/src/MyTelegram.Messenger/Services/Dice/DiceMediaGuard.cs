namespace MyTelegram.Messenger.Services.Dice;

/// <summary>
/// Where a <a href="https://corefork.telegram.org/api/dice">dice</a> may not go. A dice is not an ordinary
/// media: its value is minted by the server at send time, so every path that converts an
/// <c>InputMedia</c> outside of an actual send would either roll a value nobody asked for or roll a second
/// one over an existing message.
/// </summary>
/// <remarks>
/// The list mirrors what TDLib records for <c>MessageContentType::Dice</c> in
/// <c>MessageContentType.cpp</c>: <c>is_editable_message_content</c>, <c>is_allowed_media_group_content</c>
/// and <c>can_be_local_message_content</c> are all false, so no official client offers any of these — the
/// checks exist so a client that tries anyway is refused instead of silently served a fresh roll.
/// </remarks>
public static class DiceMediaGuard
{
    /// <summary>Whether this input media is a dice in either of its two forms.</summary>
    public static bool IsDice(IInputMedia? media)
    {
        return media is TInputMediaDice or TInputMediaStakeDice;
    }

    /// <summary>
    /// Refuses a dice with <c>MEDIA_INVALID</c>. Used by <c>messages.editMessage</c>,
    /// <c>messages.sendMultiMedia</c>, <c>messages.saveDraft</c> and <c>messages.uploadMedia</c>.
    /// </summary>
    public static void ThrowIfDice(IInputMedia? media)
    {
        if (IsDice(media))
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
        }
    }
}
