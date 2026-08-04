namespace MyTelegram;

/// <summary>
/// An <a href="https://core.telegram.org/api/emoji-status">emoji status</a> of a user or channel.
/// </summary>
/// <param name="DocumentId">The custom emoji shown next to the name.</param>
/// <param name="Until">Unixtime after which the status expires, if any.</param>
/// <param name="CollectibleId">
/// Set when the status comes from a <a href="https://core.telegram.org/api/gifts#collectible-gifts">collectible gift</a>,
/// in which case clients must receive an <c>emojiStatusCollectible</c> instead of a plain <c>emojiStatus</c>.
/// </param>
public record EmojiStatus(long DocumentId, int? Until = null, long? CollectibleId = null);
