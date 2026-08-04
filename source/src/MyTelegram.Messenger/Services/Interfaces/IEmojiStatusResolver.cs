namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Turns the stored <see cref="EmojiStatus"/> of a user or channel into the TL
/// <a href="https://core.telegram.org/api/emoji-status">emoji status</a> clients expect: plain
/// <c>emojiStatus</c>, the richer <c>emojiStatusCollectible</c> when it comes from a collectible
/// gift, or nothing at all once it has expired.
/// </summary>
public interface IEmojiStatusResolver
{
    /// <summary>
    /// Resolves a single status. Returns <c>null</c> when there is no status or when its
    /// <c>until</c> has already passed, so expired statuses stop being advertised.
    /// </summary>
    Task<IEmojiStatus?> ResolveAsync(EmojiStatus? emojiStatus, int layer = 0);

    /// <summary>
    /// Synchronous counterpart of <see cref="ResolveAsync"/>, for the synchronous peer converters.
    /// Only hits the database when the status actually comes from a collectible gift.
    /// </summary>
    IEmojiStatus? Resolve(EmojiStatus? emojiStatus, int layer = 0);

    /// <summary>
    /// Resolves several statuses with a single database round trip per collection, for the peer
    /// lists returned by most RPC methods.
    /// </summary>
    Task<Dictionary<long, IEmojiStatus>> ResolveManyAsync(
        IReadOnlyCollection<KeyValuePair<long, EmojiStatus>> emojiStatuses,
        int layer = 0);

    /// <summary>Whether the status has expired and must no longer be shown.</summary>
    bool IsExpired(EmojiStatus? emojiStatus);
}
