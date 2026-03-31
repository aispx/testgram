using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class DeleteQuickReplyShortcutHandler : RpcResultObjectHandler<RequestDeleteQuickReplyShortcut, IBool>
{
    private readonly IMongoDatabase _database;

    public DeleteQuickReplyShortcutHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDeleteQuickReplyShortcut obj)
    {
        var userId = input.UserId;
        var shortcutId = obj.ShortcutId;

        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("ShortcutId", shortcutId)
        );

        var result = await collection.DeleteOneAsync(filter);

        if (result.DeletedCount == 0)
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
