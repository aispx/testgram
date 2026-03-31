using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Core;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class SendQuickReplyMessagesHandler : RpcResultObjectHandler<RequestSendQuickReplyMessages, IUpdates>
{
    private readonly IMongoDatabase _database;
    private readonly IMessageAppService _messageAppService;
    private readonly IPeerHelper _peerHelper;

    public SendQuickReplyMessagesHandler(
        IMongoDatabase database,
        IMessageAppService messageAppService,
        IPeerHelper peerHelper)
    {
        _database = database;
        _messageAppService = messageAppService;
        _peerHelper = peerHelper;
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendQuickReplyMessages obj)
    {
        var userId = input.UserId;
        var shortcutId = obj.ShortcutId;

        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("ShortcutId", shortcutId)
        );
        
        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        if (doc == null)
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var storedMessageIds = new List<int>();
        if (doc != null && doc.Contains("MessageIds"))
        {
            var msgIds = doc["MessageIds"].AsBsonArray;
            foreach (var msgId in msgIds)
            {
                storedMessageIds.Add(msgId.AsInt32);
            }
        }

        var messageIdsToSend = obj.Id != null && obj.Id.Count > 0 
            ? storedMessageIds.Where(m => obj.Id.Contains(m)).ToList()
            : storedMessageIds;

        if (messageIdsToSend.Count == 0)
        {
            return new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = CurrentDate
            };
        }

        var messagesCollection = _database.GetCollection<BsonDocument>("messages");
        var msgFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerPeerId", userId),
            Builders<BsonDocument>.Filter.In("MessageId", messageIdsToSend)
        );
        
        var messages = await messagesCollection.Find(msgFilter).ToListAsync();

        var toPeer = _peerHelper.GetPeer(obj.Peer, userId);
        var sendMessageInputs = new List<SendMessageInput>();
        var randomIdIndex = 0;

        foreach (var msgDoc in messages)
        {
            var messageText = msgDoc.GetValue("Message", "").AsString;
            var randomId = randomIdIndex < obj.RandomId.Count 
                ? obj.RandomId[randomIdIndex++] 
                : Random.Shared.NextInt64();

            var sendMessageInput = new SendMessageInput(
                input.ToRequestInfo(),
                userId,
                toPeer,
                messageText,
                randomId,
                isSendQuickReplyMessage: true
            );
            
            sendMessageInputs.Add(sendMessageInput);
        }

        if (sendMessageInputs.Count > 0)
        {
            await _messageAppService.SendMessageAsync(sendMessageInputs);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}
