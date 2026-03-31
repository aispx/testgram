using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class EditQuickReplyShortcutHandler : RpcResultObjectHandler<RequestEditQuickReplyShortcut, IBool>
{
    private readonly IMongoDatabase _database;

    public EditQuickReplyShortcutHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestEditQuickReplyShortcut obj)
    {
        var userId = input.UserId;
        var shortcutId = obj.ShortcutId;
        var newTitle = obj.Shortcut;

        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("ShortcutId", shortcutId)
        );

        var update = Builders<BsonDocument>.Update.Set("Title", newTitle);
        var result = await collection.UpdateOneAsync(filter, update);

        if (result.MatchedCount == 0)
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
