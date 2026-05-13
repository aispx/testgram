using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal static class MessageSearchMongoHelper
{
    public static (Peer Peer, Peer? SavedPeer, long OwnerPeerId) ResolveScope(
        IPeerHelper peerHelper,
        IRequestInput input,
        IInputPeer peer,
        IInputPeer? savedPeerId)
    {
        var resolvedPeer = peerHelper.GetPeer(peer, input.UserId);
        var resolvedSavedPeer = savedPeerId == null ? null : peerHelper.GetPeer(savedPeerId, input.UserId);
        var ownerPeerId = resolvedPeer.PeerType == PeerType.Channel ? resolvedPeer.PeerId : input.UserId;
        return (resolvedPeer, resolvedSavedPeer, ownerPeerId);
    }

    public static FilterDefinition<BsonDocument> BuildFilter(
        IRequestInput input,
        IPeerHelper peerHelper,
        IInputPeer peerInput,
        IInputPeer? savedPeerInput,
        int? topMsgId,
        IMessagesFilter? filter,
        int? offsetId = null,
        int? offsetDate = null)
    {
        var builder = Builders<BsonDocument>.Filter;
        var (peer, savedPeer, ownerPeerId) = ResolveScope(peerHelper, input, peerInput, savedPeerInput);
        var filterDef = builder.And(
            builder.Eq("OwnerPeerId", ownerPeerId),
            builder.Eq("ToPeerType", peer.PeerType.ToString()),
            builder.Eq("ToPeerId", peer.PeerId)
        );

        if (savedPeer != null)
        {
            filterDef &= builder.Eq("SavedPeerId.PeerType", savedPeer.PeerType.ToString());
            filterDef &= builder.Eq("SavedPeerId.PeerId", savedPeer.PeerId);
        }
        else if (topMsgId.HasValue)
        {
            filterDef &= builder.Eq("TopMsgId", topMsgId.Value);
        }

        if (offsetId.HasValue && offsetId.Value > 0)
        {
            filterDef &= builder.Lt("MessageId", offsetId.Value);
        }

        if (offsetDate.HasValue && offsetDate.Value > 0)
        {
            filterDef &= builder.Lte("Date", offsetDate.Value);
        }

        var messageType = GetMessageType(filter);
        if (messageType == MessageType.Pinned)
        {
            filterDef &= builder.Eq("Pinned", true);
        }
        else if (messageType != MessageType.Unknown)
        {
            filterDef &= builder.Eq("MessageType", messageType.ToString());
        }

        return filterDef;
    }

    public static MessageType GetMessageType(IMessagesFilter? filter)
    {
        return filter switch
        {
            null => MessageType.Unknown,
            TInputMessagesFilterChatPhotos => MessageType.Photo,
            TInputMessagesFilterContacts => MessageType.Contacts,
            TInputMessagesFilterDocument => MessageType.Document,
            TInputMessagesFilterEmpty => MessageType.Unknown,
            TInputMessagesFilterGeo => MessageType.Geo,
            TInputMessagesFilterGif => MessageType.Photo,
            TInputMessagesFilterMusic => MessageType.Music,
            TInputMessagesFilterMyMentions => MessageType.Unknown,
            TInputMessagesFilterPhoneCalls => MessageType.PhoneCall,
            TInputMessagesFilterPhotos => MessageType.Photo,
            TInputMessagesFilterPhotoVideo => MessageType.Video,
            TInputMessagesFilterPinned => MessageType.Pinned,
            TInputMessagesFilterPoll => MessageType.Poll,
            TInputMessagesFilterRoundVideo => MessageType.Video,
            TInputMessagesFilterRoundVoice => MessageType.Voice,
            TInputMessagesFilterUrl => MessageType.Url,
            TInputMessagesFilterVideo => MessageType.Video,
            TInputMessagesFilterVoice => MessageType.Voice,
            _ => MessageType.Unknown
        };
    }

    public static long CalcHash(long hash, long id)
    {
        hash ^= hash >> 21;
        hash ^= hash << 35;
        hash ^= hash >> 4;
        return hash + id;
    }
}
