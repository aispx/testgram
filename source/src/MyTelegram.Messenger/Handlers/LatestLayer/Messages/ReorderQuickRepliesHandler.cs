using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class ReorderQuickRepliesHandler : RpcResultObjectHandler<RequestReorderQuickReplies, IBool>
{
    private readonly IMongoDatabase _database;

    public ReorderQuickRepliesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestReorderQuickReplies obj)
    {
        var userId = input.UserId;
        var order = obj.Order.ToList();

        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        
        for (int i = 0; i < order.Count; i++)
        {
            var shortcutId = order[i];
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", userId),
                Builders<BsonDocument>.Filter.Eq("ShortcutId", shortcutId)
            );
            var update = Builders<BsonDocument>.Update.Set("Order", i);
            await collection.UpdateOneAsync(filter, update);
        }

        return new TBoolTrue();
    }
}
