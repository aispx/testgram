using System.Globalization;

namespace MyTelegram;

public static class ReactionReadState
{
    private const string KeyPrefix = "read_reactions";

    public static string GetKey(Peer peer, int? topMsgId = null, Peer? savedPeerId = null)
    {
        if (savedPeerId != null)
        {
            return $"{KeyPrefix}:{(int)peer.PeerType}:{peer.PeerId}:saved:{(int)savedPeerId.PeerType}:{savedPeerId.PeerId}";
        }

        if (topMsgId.HasValue)
        {
            return $"{KeyPrefix}:{(int)peer.PeerType}:{peer.PeerId}:topic:{topMsgId.Value}";
        }

        return $"{KeyPrefix}:{(int)peer.PeerType}:{peer.PeerId}";
    }

    public static int ParseReadDate(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var date) ? date : 0;
    }

    public static bool IsUnread(MessagePeerReaction reaction, long userId, int readDate)
    {
        return reaction.SenderUserId != userId && reaction.Date > readDate;
    }
}
