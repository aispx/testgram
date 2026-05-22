using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Send scheduled messages right away
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendScheduledMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendScheduledMessagesHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendScheduledMessages, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendScheduledMessages obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Load scheduled messages from MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("SenderUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("PeerType", peer.PeerType.ToString()),
            Builders<BsonDocument>.Filter.In("ScheduledMessageId", obj.Id)
        );

        var docs = await collection.Find(filter).ToListAsync();
        if (docs.Count == 0)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var sendInputs = new List<SendMessageInput>();
        var scheduledIds = new List<int>();

        foreach (var doc in docs)
        {
            var entities = doc.Contains("Entities") && !doc["Entities"].IsBsonNull
                ? System.Text.Json.JsonSerializer.Deserialize<TVector<IMessageEntity>>(doc["Entities"].AsString)
                : null;

            var sendInput = new SendMessageInput(
                input.ToRequestInfo(),
                input.UserId,
                peer,
                doc["Message"].AsString,
                doc["RandomId"].AsInt64,
                entities: entities,
                sendMessageType: SendMessageType.Text,
                messageType: MessageType.Text,
                silent: doc.Contains("Silent") && doc["Silent"].AsBoolean,
                noForwards: doc.Contains("NoForwards") && doc["NoForwards"].AsBoolean
            );

            sendInputs.Add(sendInput);
            scheduledIds.Add(doc["ScheduledMessageId"].AsInt32);
        }

        // Send messages immediately
        await messageAppService.SendMessageAsync(sendInputs);

        // Delete from scheduled queue
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
                    Messages = new TVector<int>(scheduledIds)
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
