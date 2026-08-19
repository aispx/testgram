namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Resolves the latest pinned message id of a peer, which is what <c>pinned_msg_id</c> of
/// <c>userFull</c>/<c>chatFull</c>/<c>channelFull</c> carries. The full list of pinned messages is
/// fetched separately with messages.search + inputMessagesFilterPinned.
/// See https://corefork.telegram.org/api/pin#getting-pinned-messages
/// </summary>
public interface IPinnedMessageResolver
{
    /// <summary>Latest pinned message of a channel/supergroup, or null when nothing is pinned.</summary>
    Task<int?> GetChannelPinnedMsgIdAsync(long channelId);

    /// <summary>
    /// Latest message the user has pinned in a one-to-one chat. Read from the caller's own copy of the
    /// history, so a message pinned only on the other side (<c>pm_oneside</c>) is not reported here.
    /// </summary>
    Task<int?> GetPrivateChatPinnedMsgIdAsync(long selfUserId, long targetUserId);
}
