using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Admins with <a href="https://corefork.telegram.org/constructor/chatAdminRights">ban_users admin rights »</a> may allow users that apply a certain number of <a href="https://corefork.telegram.org/api/boost">booosts »</a> to the group to bypass <a href="https://corefork.telegram.org/method/channels.toggleSlowMode">slow mode »</a> and <a href="https://corefork.telegram.org/api/rights#default-rights">other »</a> supergroup restrictions, see <a href="https://corefork.telegram.org/api/boost#bypass-slowmode-and-chat-restrictions">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.setBoostsToUnblockRestrictions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetBoostsToUnblockRestrictionsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSetBoostsToUnblockRestrictions, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestSetBoostsToUnblockRestrictions obj)
    {
        // Convert IInputChannel to IInputPeer
        IInputPeer inputPeer;
        if (obj.Channel is TInputChannel inputChannel)
        {
            inputPeer = new TInputPeerChannel { ChannelId = inputChannel.ChannelId, AccessHash = inputChannel.AccessHash };
        }
        else if (obj.Channel is TInputChannelFromMessage inputChannelFromMessage)
        {
            inputPeer = new TInputPeerChannelFromMessage
            {
                Peer = inputChannelFromMessage.Peer,
                MsgId = inputChannelFromMessage.MsgId,
                ChannelId = inputChannelFromMessage.ChannelId
            };
        }
        else
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;
        var boosts = obj.Boosts;

        // Update MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update.Set("BoostsToUnblockRestrictions", boosts);
        await collection.UpdateOneAsync(filter, update);

        return new TUpdates
        {
            Chats = new TVector<IChat>(),
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}