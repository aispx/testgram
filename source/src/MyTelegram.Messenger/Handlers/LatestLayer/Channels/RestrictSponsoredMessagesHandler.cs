using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Disable ads on the specified channel, for all users.Available only after reaching at least the <a href="https://corefork.telegram.org/api/boost">boost level »</a> specified in the <a href="https://corefork.telegram.org/api/config#channel-restrict-sponsored-level-min"><code>channel_restrict_sponsored_level_min</code> »</a> config parameter.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.restrictSponsoredMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RestrictSponsoredMessagesHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestRestrictSponsoredMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestRestrictSponsoredMessages obj)
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
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var channelId = peer.PeerId;
        var restricted = obj.Restricted;

        // Update MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update.Set("RestrictedSponsored", restricted);
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