using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Reorder usernames associated with the currently logged-in user
/// See https://core.telegram.org/method/account.reorderUsernames
/// </summary>
internal sealed class ReorderUsernamesHandler : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestReorderUsernames, IBool>
{
    private readonly IMongoDatabase _database;

    public ReorderUsernamesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Account.RequestReorderUsernames obj)
    {
        var userId = input.UserId;

        // Get user from MongoDB
        var userCollection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCollection.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Get current usernames
        var usernames = user.Contains("Usernames") && !user["Usernames"].IsBsonNull
            ? user["Usernames"].AsBsonArray
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

        // Validate order contains all active usernames
        var orderLower = obj.Order.Select(u => u.ToLower()).ToList();
        var activeSet = new HashSet<string>(activeUsernames);
        var orderSet = new HashSet<string>(orderLower);

        if (!activeSet.SetEquals(orderSet))
        {
            RpcErrors.RpcErrors400.OrderInvalid.ThrowRpcError();
        }

        // Reorder usernames: active ones first in specified order, then inactive ones
        var reorderedUsernames = new BsonArray();
        
        // Add active usernames in new order
        foreach (var username in obj.Order)
        {
            if (usernameMap.TryGetValue(username, out var doc))
            {
                reorderedUsernames.Add(doc);
            }
        }
        
        // Add inactive usernames (keep their original order)
        foreach (var item in usernames)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Username"))
                {
                    var username = doc["Username"].AsString;
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
        await userCollection.UpdateOneAsync(userFilter, update);

        return new TBoolTrue();
    }
}
