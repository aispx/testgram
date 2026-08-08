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

    /// <summary>
    /// For a channel scope, verifies the caller is a member before any count/position/calendar query
    /// runs. <see cref="ResolveScope"/> derives <c>OwnerPeerId</c> from the client-supplied peer and
    /// <see cref="IPeerHelper.GetPeer"/> does not validate the access hash, so without this gate these
    /// read paths return message counts and dates for private channels the caller never joined —
    /// the same leak messages.getHistory already blocks. Returns true when an RPC error was sent.
    /// </summary>
    public static async Task<bool> SendRpcErrorIfNotVisibleAsync(
        IPeerHelper peerHelper,
        IChannelAppService channelAppService,
        IRequestInput input,
        IInputPeer peerInput)
    {
        var peer = peerHelper.GetPeer(peerInput, input.UserId);
        if (peer.PeerType != PeerType.Channel)
        {
            return false;
        }

        var channelReadModel = await channelAppService.GetAsync((long?)peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        return await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!);
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
            // Enums are persisted as their numeric value, not as their name.
            builder.Eq("ToPeerType", (int)peer.PeerType),
            builder.Eq("ToPeerId", peer.PeerId)
        );

        if (savedPeer != null)
        {
            filterDef &= builder.Eq("SavedPeerId.PeerType", (int)savedPeer.PeerType);
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

        if (MessageFilterHelper.IsPinnedFilter(filter))
        {
            filterDef &= builder.Eq("Pinned", true);
        }
        else
        {
            var messageTypes = MessageFilterHelper.GetMessageTypes(filter);
            if (messageTypes.Count > 0)
            {
                filterDef &= builder.In("MessageType", messageTypes.Select(p => (int)p));
            }
        }

        return filterDef;
    }

    public static long CalcHash(long hash, long id)
    {
        hash ^= hash >> 21;
        hash ^= hash << 35;
        hash ^= hash >> 4;
        return hash + id;
    }
}
