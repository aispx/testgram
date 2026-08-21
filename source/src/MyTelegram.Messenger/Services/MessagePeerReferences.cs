namespace MyTelegram.Messenger.Services;

/// <summary>
/// The peers a message names, and therefore the peers a client can legitimately cite that message as
/// context for. See https://corefork.telegram.org/api/min#example
/// <para>
/// This is deliberately the single source of truth for two opposite directions of the same rule:
/// <see cref="IFromMessagePeerResolver"/> uses it to decide whether an incoming
/// <c>input*FromMessage</c> citation holds up, and <see cref="IMinConstructorReducer"/> uses it to
/// decide which peers may be sent out as <c>min</c> in the first place. Sharing the predicate is what
/// makes the two agree: the server only ever marks a peer <c>min</c> when the client will be able to
/// build a citation for it that the server itself will accept back.
/// </para>
/// </summary>
public static class MessagePeerReferences
{
    /// <summary>
    /// The users a min constructor may legitimately have been attached to: the sender, anybody in the
    /// forward header, the other side of the conversation, anybody mentioned by name, the
    /// correspondent a saved copy names and the repliers shown on a comment thread.
    /// </summary>
    public static bool ReferencesUser(IMessageReadModel message, long userId)
    {
        if (message.SenderUserId == userId || message.SenderPeerId == userId)
        {
            return true;
        }

        if (message.ToPeerType is PeerType.User or PeerType.Self && message.ToPeerId == userId)
        {
            return true;
        }

        if (message.MentionedUserIds?.Contains(userId) == true)
        {
            return true;
        }

        // Saved Messages and monoforums keep the original correspondent beside the copy, so that
        // peer is one the client saw in this message too.
        if (IsUserPeer(message.SavedPeerId, userId))
        {
            return true;
        }

        // The avatars a comment thread shows next to the reply count come from the message too.
        if (message.Reply?.RecentRepliers?.Any(p => IsUserPeer(p, userId)) == true)
        {
            return true;
        }

        var fwdHeader = message.FwdHeader;
        if (fwdHeader != null)
        {
            if (IsUserPeer(fwdHeader.FromId, userId) ||
                IsUserPeer(fwdHeader.SavedFromId, userId) ||
                IsUserPeer(fwdHeader.SavedFromPeer, userId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The channels a min constructor may legitimately have been attached to: the chat the message is
    /// in, the channel it was sent as, the discussion group/linked channel pair and anything in the
    /// forward header.
    /// </summary>
    public static bool ReferencesChannel(IMessageReadModel message, long channelId)
    {
        if (message.OwnerPeerId == channelId || message.SenderPeerId == channelId)
        {
            return true;
        }

        if (message.ToPeerType == PeerType.Channel && message.ToPeerId == channelId)
        {
            return true;
        }

        if (message.LinkedChannelId == channelId || message.PostChannelId == channelId)
        {
            return true;
        }

        if (message.SendAs is { PeerType: PeerType.Channel } sendAs && sendAs.PeerId == channelId)
        {
            return true;
        }

        // A post carries the discussion group its comments live in, and a saved copy carries the
        // channel it came from.
        if (message.Reply?.ChannelId == channelId || IsChannelPeer(message.SavedPeerId, channelId))
        {
            return true;
        }

        // Anonymous admins reply as the channel, so a replier can be one.
        if (message.Reply?.RecentRepliers?.Any(p => IsChannelPeer(p, channelId)) == true)
        {
            return true;
        }

        var fwdHeader = message.FwdHeader;
        if (fwdHeader != null)
        {
            if (IsChannelPeer(fwdHeader.FromId, channelId) ||
                IsChannelPeer(fwdHeader.SavedFromId, channelId) ||
                IsChannelPeer(fwdHeader.SavedFromPeer, channelId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUserPeer(Peer? peer, long userId) =>
        peer is { PeerType: PeerType.User or PeerType.Self } && peer.PeerId == userId;

    private static bool IsChannelPeer(Peer? peer, long channelId) =>
        peer is { PeerType: PeerType.Channel } && peer.PeerId == channelId;
}
