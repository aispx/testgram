namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Authorization for <a href="https://corefork.telegram.org/api/monoforum">monoforum topic</a> reads.
/// <para>
/// A monoforum channel holds one topic per user, each topic being that user's private conversation with
/// the channel. The topic peer arrives in the request, so resolving it alone is not enough: without an
/// ownership check a caller can name any third party as the topic peer and read that person's private
/// conversation. Only the topic owner and the admins of the linked broadcast channel may read a topic.
/// </para>
/// </summary>
internal static class MonoforumAccessHelper
{
    /// <summary>
    /// Throws <c>CHAT_ADMIN_REQUIRED</c> unless the caller owns <paramref name="topicPeer"/> or holds the
    /// <c>ManageDirectMessages</c> right on the broadcast side of <paramref name="monoforum"/>.
    /// </summary>
    public static async Task EnsureCanReadTopicAsync(
        IChannelReadModel monoforum,
        Peer topicPeer,
        long userId,
        IChannelAppService channelAppService)
    {
        // A topic is keyed by a user peer; anything else cannot own one.
        if (topicPeer.PeerType is not (PeerType.User or PeerType.Self))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // The caller reading their own conversation with the channel.
        if (topicPeer.PeerId == userId)
        {
            return;
        }

        if (!await CanManageDirectMessagesAsync(monoforum, userId, channelAppService))
        {
            RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
        }
    }

    /// <summary>
    /// Throws <c>CHAT_ADMIN_REQUIRED</c> unless the caller may see every topic of the monoforum. Used by
    /// the listing methods, which return all topics rather than one named topic.
    /// </summary>
    public static async Task EnsureCanReadAllTopicsAsync(
        IChannelReadModel monoforum,
        long userId,
        IChannelAppService channelAppService)
    {
        if (!await CanManageDirectMessagesAsync(monoforum, userId, channelAppService))
        {
            RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
        }
    }

    /// <summary>
    /// Non-throwing form of <see cref="EnsureCanReadAllTopicsAsync"/>, for the listing methods that
    /// narrow their scope to the caller's own topic instead of failing outright.
    /// </summary>
    public static Task<bool> CanManageAllTopicsAsync(
        IChannelReadModel monoforum,
        long userId,
        IChannelAppService channelAppService)
    {
        return CanManageDirectMessagesAsync(monoforum, userId, channelAppService);
    }

    /// <summary>True when <paramref name="topicPeer"/> is the caller's own topic.</summary>
    public static bool IsOwnTopic(Peer? topicPeer, long userId)
    {
        return topicPeer is not null
               && topicPeer.PeerType is PeerType.User or PeerType.Self
               && topicPeer.PeerId == userId;
    }

    /// <summary>
    /// True when the caller is the creator of, or holds <c>ManageDirectMessages</c> on, the broadcast
    /// channel the monoforum is attached to. The rights live on the broadcast side, so
    /// <c>LinkedMonoforumId</c> is followed back before they are read — matching
    /// <see cref="ToggleSuggestedPostApprovalHandler"/>.
    /// </summary>
    private static async Task<bool> CanManageDirectMessagesAsync(
        IChannelReadModel monoforum,
        long userId,
        IChannelAppService channelAppService)
    {
        if (monoforum.CreatorId == userId)
        {
            return true;
        }

        var linkedChannel = monoforum.LinkedMonoforumId.HasValue
            ? await channelAppService.GetAsync(monoforum.LinkedMonoforumId.Value)
            : null;
        if (linkedChannel == null)
        {
            return false;
        }

        if (linkedChannel.CreatorId == userId)
        {
            return true;
        }

        var admin = linkedChannel.AdminList.FirstOrDefault(p => p.UserId == userId);
        return admin?.AdminRights.ManageDirectMessages == true;
    }
}
