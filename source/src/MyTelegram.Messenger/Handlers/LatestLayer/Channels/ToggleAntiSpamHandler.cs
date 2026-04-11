using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Enable or disable the <a href="https://corefork.telegram.org/api/antispam">native antispam system</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleAntiSpam"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleAntiSpamHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleAntiSpam, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestToggleAntiSpam obj)
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
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var currentAntiSpam = channelDoc.Contains("AntiSpamEnabled") && channelDoc["AntiSpamEnabled"].AsBoolean;
        if (currentAntiSpam == obj.Enabled)
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();

        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update.Set("AntiSpamEnabled", obj.Enabled);
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