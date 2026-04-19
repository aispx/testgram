using MyTelegram.Schema.Messages;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetRecentReactionsHandler(
    IQueryProcessor queryProcessor,
    IMongoDatabase database)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentReactions, MyTelegram.Schema.Messages.IReactions>
{
    private const string RecentKey = "recent_reactions";

    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetRecentReactions obj)
    {
        var limit = obj.Limit > 0 ? obj.Limit : 8;
        var config = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));

        IReadOnlyList<IReaction> reactions;
        if (config?.Value is { Length: > 0 })
        {
            reactions = config.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Take(limit)
                .Select(e => (IReaction)new TReactionEmoji { Emoticon = e })
                .ToList();
        }
        else
        {
            // Load default reactions from MongoDB
            var collection = database.GetCollection<BsonDocument>("reactions");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("Inactive", false),
                Builders<BsonDocument>.Filter.Eq("Premium", false)
            );
            var sort = Builders<BsonDocument>.Sort.Ascending("Order");
            var reactionDocs = await collection.Find(filter).Sort(sort).Limit(limit).ToListAsync();

            reactions = reactionDocs
                .Select(doc => (IReaction)new TReactionEmoji { Emoticon = doc["Reaction"].AsString })
                .ToList();
        }

        return new TReactions { Hash = 0, Reactions = new TVector<IReaction>(reactions.ToList()) };
    }
}
