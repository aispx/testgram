using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class CheckQuickReplyShortcutHandler : RpcResultObjectHandler<RequestCheckQuickReplyShortcut, IBool>
{
    private readonly IMongoDatabase _database;
    private const int MaxShortcuts = 100;
    private const int MaxMessagesPerShortcut = 100;

    public CheckQuickReplyShortcutHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestCheckQuickReplyShortcut obj)
    {
        var userId = input.UserId;
        var shortcutName = obj.Shortcut;

        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var count = await collection.CountDocumentsAsync(filter);
        
        if (count >= MaxShortcuts)
        {
            RpcErrors.RpcErrors400.QuickRepliesTooMuch.ThrowRpcError();
        }

        if (!string.IsNullOrEmpty(shortcutName))
        {
            var nameFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("Title", shortcutName)
            );
            var existing = await collection.Find(nameFilter).FirstOrDefaultAsync();
            if (existing != null)
            {
                var msgCount = existing.GetValue("Count", 0).AsInt32;
                if (msgCount >= MaxMessagesPerShortcut)
                {
                    RpcErrors.RpcErrors400.ReplyMessagesTooMuch.ThrowRpcError();
                }
            }
        }

        return new TBoolTrue();
    }
}
