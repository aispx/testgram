using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class DeleteQuickReplyMessagesHandler : RpcResultObjectHandler<RequestDeleteQuickReplyMessages, IUpdates>
{
    private readonly IMongoDatabase _database;

    public DeleteQuickReplyMessagesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestDeleteQuickReplyMessages obj)
    {
        var userId = input.UserId;
        var shortcutId = obj.ShortcutId;
        var messageIdsToDelete = obj.Id.ToList();

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

        var currentMessageIds = new List<int>();
        if (doc.Contains("MessageIds"))
        {
            var msgIds = doc["MessageIds"].AsBsonArray;
            foreach (var msgId in msgIds)
            {
                currentMessageIds.Add(msgId.AsInt32);
            }
        }

        var remainingIds = currentMessageIds.Except(messageIdsToDelete).ToList();
        var deletedCount = currentMessageIds.Count - remainingIds.Count;

        var update = Builders<BsonDocument>.Update.Set("MessageIds", remainingIds)
            .Set("Count", remainingIds.Count);
        
        if (remainingIds.Count > 0)
        {
            update = update.Set("TopMessage", remainingIds.Last());
        }
        
        await collection.UpdateOneAsync(filter, update);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateDeleteQuickReplyMessages
                {
                    ShortcutId = shortcutId,
                    Messages = new TVector<int>(messageIdsToDelete)
                }
            },
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}
