namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Turns the TL <c>EmojiStatus</c> a client sent into the stored
/// <a href="https://core.telegram.org/api/emoji-status">emoji status</a>, validating collectible
/// ownership on the way.
/// </summary>
public interface IEmojiStatusInputResolver
{
    /// <summary>
    /// Resolves the requested status for <paramref name="ownerUserId"/>. Returns <c>null</c> for
    /// <c>emojiStatusEmpty</c> (the status is being cleared). Throws <c>COLLECTIBLE_INVALID</c> when a
    /// collectible is not owned by that user, and <c>DOCUMENT_INVALID</c> for an unusable constructor —
    /// including <c>emojiStatusCollectible</c>, which per the API must be converted to
    /// <c>inputEmojiStatusCollectible</c> by the client first.
    /// </summary>
    Task<EmojiStatus?> ResolveAsync(IEmojiStatus? emojiStatus, long ownerUserId);
}
