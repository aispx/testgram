using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Reorder usernames associated with a bot
/// See https://core.telegram.org/method/bots.reorderUsernames
/// </summary>
internal sealed class ReorderUsernamesHandler : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestReorderUsernames, IBool>
{
    private readonly IMongoDatabase _database;

    public ReorderUsernamesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Bots.RequestReorderUsernames obj)
    {
        if (obj.Bot is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var botUserId = inputUser.UserId;

        // Get bot from MongoDB
        var userCollection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var botFilter = Builders<BsonDocument>.Filter.Eq("UserId", botUserId);
        var bot = await userCollection.Find(botFilter).FirstOrDefaultAsync();

        if (bot == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        // Get current usernames
        var usernames = bot.Contains("Usernames") && !bot["Usernames"].IsBsonNull
            ? bot["Usernames"].AsBsonArray
            : new BsonArray();

        // Build username map
        var usernameMap = new Dictionary<string, BsonDocument>(StringComparer.OrdinalIgnoreCase);
        var activeUsernames = new List<string>();
        
        foreach (var item in usernames)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Username"))
                {
                    var username = doc["Username"].AsString;
                    usernameMap[username] = doc;
                    
                    if (doc.Contains("Active") && doc["Active"].AsBoolean)
                    {
                        activeUsernames.Add(username.ToLower());
                    }
                }
            }
        }

        // Validate order
        var orderLower = obj.Order.Select(u => u.ToLower()).ToList();
        var activeSet = new HashSet<string>(activeUsernames);
        var orderSet = new HashSet<string>(orderLower);

        if (!activeSet.SetEquals(orderSet))
        {
            RpcErrors.RpcErrors400.OrderInvalid.ThrowRpcError();
            return null!;
        }

        // Reorder usernames
        var reorderedUsernames = new BsonArray();
        
        foreach (var username in obj.Order)
        {
            if (usernameMap.TryGetValue(username, out var doc))
            {
                reorderedUsernames.Add(doc);
            }
        }
        
        foreach (var item in usernames)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Username"))
                {
                    var isActive = doc.Contains("Active") && doc["Active"].AsBoolean;
                    if (!isActive)
                    {
                        reorderedUsernames.Add(doc);
                    }
                }
            }
        }

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("Usernames", reorderedUsernames);
        await userCollection.UpdateOneAsync(botFilter, update);

        return new TBoolTrue();
    }
}
