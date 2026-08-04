namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Validates the custom emoji a channel or supergroup may use as its
/// <a href="https://core.telegram.org/api/emoji-status">emoji status</a>, and serves the
/// restricted list returned by <c>account.getChannelRestrictedStatusEmojis</c>.
/// </summary>
public interface IChannelEmojiStatusValidator
{
    /// <summary>
    /// Custom emoji document IDs that are allowed as a channel emoji status, i.e. those belonging to
    /// a sticker set flagged <c>channel_emoji_status</c> and not present in the restricted list.
    /// </summary>
    Task<List<long>> GetAllowedDocumentIdsAsync();

    /// <summary>
    /// Custom emoji document IDs that cannot be used as a channel emoji status, stored in the
    /// <c>channel_restricted_status_emojis</c> collection.
    /// </summary>
    Task<List<long>> GetRestrictedDocumentIdsAsync();

    /// <summary>
    /// Whether the given custom emoji may be set as a channel emoji status. When no sticker set is
    /// flagged <c>channel_emoji_status</c> the check passes, so a server without a curated channel
    /// status pack does not reject every status.
    /// </summary>
    Task<bool> IsAllowedAsync(long documentId);
}
