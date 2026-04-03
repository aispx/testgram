using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Activate or deactivate a purchased fragment.com username
/// See https://core.telegram.org/method/account.toggleUsername
/// </summary>
internal sealed class ToggleUsernameHandler : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestToggleUsername, IBool>
{
    private readonly IMongoDatabase _database;

    public ToggleUsernameHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Account.RequestToggleUsername obj)
    {
        var userId = input.UserId;
        var username = obj.Username.ToLower();

        // Get user from MongoDB
        var userCollection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCollection.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Get current usernames from UsernamesV2
        var usernamesV2 = user.Contains("UsernamesV2") && !user["UsernamesV2"].IsBsonNull
            ? user["UsernamesV2"].AsBsonArray
            : new BsonArray();

        if (usernamesV2.Count == 0)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        // Find the username
        BsonDocument? targetUsername = null;
        int targetIndex = -1;
        for (int i = 0; i < usernamesV2.Count; i++)
        {
            if (usernamesV2[i].IsBsonDocument)
            {
                var doc = usernamesV2[i].AsBsonDocument;
                if (doc.Contains("Username") && doc["Username"].AsString.ToLower() == username)
                {
                    targetUsername = doc;
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetUsername == null)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        // Check if it's editable (basic username cannot be deactivated)
        var isEditable = targetUsername.Contains("Editable") && targetUsername["Editable"].AsBoolean;
        if (isEditable && !obj.Active)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // Check current active state
        var currentActive = targetUsername.Contains("Active") && targetUsername["Active"].AsBoolean;
        if (currentActive == obj.Active)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // Count active usernames
        var activeCount = 0;
        foreach (var item in usernamesV2)
        {
            if (item.IsBsonDocument)
            {
                var doc = item.AsBsonDocument;
                if (doc.Contains("Active") && doc["Active"].AsBoolean)
                {
                    activeCount++;
                }
            }
        }

        // Check max active usernames (10)
        if (obj.Active && activeCount >= 10)
        {
            RpcErrors.RpcErrors400.UsernamesActiveTooMuch.ThrowRpcError();
            return null!;
        }

        // Update username active state
        targetUsername["Active"] = obj.Active;
        usernamesV2[targetIndex] = targetUsername;

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("UsernamesV2", usernamesV2);
        await userCollection.UpdateOneAsync(userFilter, update);

        return new TBoolTrue();
    }
}
