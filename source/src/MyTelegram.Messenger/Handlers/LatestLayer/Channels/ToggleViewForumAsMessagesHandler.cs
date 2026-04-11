using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Users may also choose to display messages from all topics of a <a href="https://corefork.telegram.org/api/forum">forum</a> as if they were sent to a normal group, using a "View as messages" setting in the local client: this setting only affects the current account, and is synced to other logged in sessions using this method.Invoking this method will update the value of the <code>view_forum_as_messages</code> flag of <a href="https://corefork.telegram.org/constructor/channelFull">channelFull</a> or <a href="https://corefork.telegram.org/constructor/dialog">dialog</a> and emit an <a href="https://corefork.telegram.org/constructor/updateChannelViewForumAsMessages">updateChannelViewForumAsMessages</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleViewForumAsMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleViewForumAsMessagesHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleViewForumAsMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestToggleViewForumAsMessages obj)
    {
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
        var dialogId = DialogId.Create(input.UserId, PeerType.Channel, channelId).Value;

        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("_id", $"dialogreadmodel-{dialogId}");
        var update = Builders<BsonDocument>.Update.Set("ViewForumAsMessages", obj.Enabled);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = false });

        var updateObj = new TUpdateChannelViewForumAsMessages
        {
            ChannelId = channelId,
            Enabled = obj.Enabled
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updateObj },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}