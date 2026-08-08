using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Toggle autotranslation in a channel, for all users: see <a href="https://corefork.telegram.org/api/translation#autotranslation-for-channels">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleAutotranslation"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleAutotranslationHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleAutotranslation, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestToggleAutotranslation obj)
    {
        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Channel, input.UserId);
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
        var enabled = obj.Enabled;

        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update.Set("TranslationsDisabled", !enabled);
        await collection.UpdateOneAsync(filter, update);

        return new TUpdates { Chats = new TVector<IChat>(), Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Date = CurrentDate };
    }
}