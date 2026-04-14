using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get scheduled messages by ID
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getScheduledMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetScheduledMessagesHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetScheduledMessages, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetScheduledMessages obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Load specific scheduled messages by ID
        var collection = mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("SenderUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("PeerId", peer.PeerId),
            Builders<BsonDocument>.Filter.Eq("PeerType", peer.PeerType.ToString()),
            Builders<BsonDocument>.Filter.In("ScheduledMessageId", obj.Id)
        );

        var docs = await collection.Find(filter).ToListAsync();

        var messages = new TVector<IMessage>();
        foreach (var doc in docs)
        {
            var message = new TMessage
            {
                Id = doc["ScheduledMessageId"].AsInt32,
                Out = true,
                FromId = new TPeerUser { UserId = input.UserId },
                PeerId = ConvertToPeer(peer),
                Date = doc["ScheduleDate"].AsInt32,
                Message = doc["Message"].AsString,
                Entities = doc.Contains("Entities") && !doc["Entities"].IsBsonNull
                    ? System.Text.Json.JsonSerializer.Deserialize<TVector<IMessageEntity>>(doc["Entities"].AsString) ?? new TVector<IMessageEntity>()
                    : new TVector<IMessageEntity>(),
                Silent = doc.Contains("Silent") && doc["Silent"].AsBoolean,
                Noforwards = doc.Contains("NoForwards") && doc["NoForwards"].AsBoolean
            };

            if (doc.Contains("ReplyToMsgId") && !doc["ReplyToMsgId"].IsBsonNull)
            {
                message.ReplyTo = new TMessageReplyHeader
                {
                    ReplyToMsgId = doc["ReplyToMsgId"].AsInt32
                };
            }

            messages.Add(message);
        }

        return new TMessages
        {
            Messages = messages,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Topics = new TVector<IForumTopic>()
        };
    }

    private static IPeer ConvertToPeer(Peer peer)
    {
        return peer.PeerType switch
        {
            PeerType.User => new TPeerUser { UserId = peer.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        };
    }
}