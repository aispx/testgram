using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Converters.ConverterServices.Messages;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetQuickReplyMessagesHandler : RpcResultObjectHandler<RequestGetQuickReplyMessages, IMessages>
{
    private readonly IMongoDatabase _database;
    private readonly IMessageAppService _messageAppService;
    private readonly IGetHistoryConverterService _getHistoryConverterService;

    public GetQuickReplyMessagesHandler(
        IMongoDatabase database,
        IMessageAppService messageAppService,
        IGetHistoryConverterService getHistoryConverterService)
    {
        _database = database;
        _messageAppService = messageAppService;
        _getHistoryConverterService = getHistoryConverterService;
    }

    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetQuickReplyMessages obj)
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

        var messageIds = new List<int>();
        if (doc != null && doc.Contains("MessageIds"))
        {
            var msgIds = doc["MessageIds"].AsBsonArray;
            foreach (var msgId in msgIds)
            {
                messageIds.Add(msgId.AsInt32);
            }
        }

        if (obj.Id != null && obj.Id.Count > 0)
        {
            messageIds = messageIds.Where(m => obj.Id.Contains(m)).ToList();
        }

        if (messageIds.Count == 0)
        {
            return new TMessages
            {
                Messages = new TVector<IMessage>(),
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        var getMessageOutput = await _messageAppService.GetMessagesAsync(
            new GetMessagesInput(userId, userId, messageIds, null) { Limit = 50 });

        return _getHistoryConverterService.ToMessages(input, getMessageOutput, input.Layer);
    }
}
