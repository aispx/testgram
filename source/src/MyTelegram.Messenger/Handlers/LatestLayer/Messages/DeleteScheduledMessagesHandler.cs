using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Delete scheduled messages
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 403 MESSAGE_DELETE_FORBIDDEN You can't delete one of the messages you tried to delete, most likely because it is a service message.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteScheduledMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteScheduledMessagesHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteScheduledMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeleteScheduledMessages obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Delete scheduled messages from MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("SenderUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("PeerType", peer.PeerType.ToString()),
            Builders<BsonDocument>.Filter.In("ScheduledMessageId", obj.Id)
        );

        await collection.DeleteManyAsync(filter);

        // Generate updateDeleteScheduledMessages
        var peerObj = peer.PeerType switch
        {
            PeerType.User => (IPeer)new TPeerUser { UserId = peer.PeerId },
            PeerType.Chat => (IPeer)new TPeerChat { ChatId = peer.PeerId },
            PeerType.Channel => (IPeer)new TPeerChannel { ChannelId = peer.PeerId },
            _ => (IPeer)new TPeerUser { UserId = peer.PeerId }
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateDeleteScheduledMessages
                {
                    Peer = peerObj,
                    Messages = obj.Id
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}