using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Reorder pinned forum topics
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// See <a href="https://corefork.telegram.org/method/channels.reorderPinnedForumTopics" />
///</summary>
internal sealed class ReorderPinnedForumTopicsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestReorderPinnedForumTopics, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestReorderPinnedForumTopics obj)
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

        // Check manage_topics permission
        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        // Update PinOrder for each topic
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");

        for (int i = 0; i < obj.Order.Count; i++)
        {
            var topicId = obj.Order[i];
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                Builders<BsonDocument>.Filter.Eq("TopicId", topicId)
            );
            var update = Builders<BsonDocument>.Update.Set("PinOrder", obj.Order.Count - i);
            await topicsCol.UpdateOneAsync(filter, update);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
