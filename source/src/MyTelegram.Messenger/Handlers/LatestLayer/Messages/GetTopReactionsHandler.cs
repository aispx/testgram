using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetTopReactionsHandler(IMongoDatabase database)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetTopReactions, MyTelegram.Schema.Messages.IReactions>
{
    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetTopReactions obj)
    {
        // Load top reactions from MongoDB
        var collection = database.GetCollection<BsonDocument>("reactions");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Inactive", false),
            Builders<BsonDocument>.Filter.Eq("Premium", false)
        );
        var sort = Builders<BsonDocument>.Sort.Ascending("Order");

        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var reactionDocs = await collection.Find(filter).Sort(sort).Limit(limit).ToListAsync();

        var reactions = reactionDocs
            .Select(doc => (IReaction)new TReactionEmoji { Emoticon = doc["Reaction"].AsString })
            .ToList();

        return new TReactions
        {
            Hash = 0,
            Reactions = new TVector<IReaction>(reactions)
        };
    }
}
