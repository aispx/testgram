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

        // Get current usernames from Usernames
        var usernames = user.Contains("Usernames") && !user["Usernames"].IsBsonNull
            ? user["Usernames"].AsBsonArray
            : new BsonArray();

        if (usernames.Count == 0)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        // Find the username
        BsonDocument? targetUsername = null;
        int targetIndex = -1;
        for (int i = 0; i < usernames.Count; i++)
        {
            if (usernames[i].IsBsonDocument)
            {
                var doc = usernames[i].AsBsonDocument;
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
        foreach (var item in usernames)
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
        usernames[targetIndex] = targetUsername;

        // Save back to MongoDB
        var update = Builders<BsonDocument>.Update.Set("Usernames", usernames);
        await userCollection.UpdateOneAsync(userFilter, update);

        // Send updateUserName to client
        var updatedUser = await userCollection.Find(userFilter).FirstOrDefaultAsync();
        if (updatedUser != null)
        {
            var firstName = updatedUser.Contains("FirstName") ? updatedUser["FirstName"].AsString : "";
            var lastName = updatedUser.Contains("LastName") ? updatedUser["LastName"].AsString : "";

            var usernamesVector = new TVector<IUsername>();
            foreach (var item in usernames)
            {
                if (item.IsBsonDocument)
                {
                    var doc = item.AsBsonDocument;
                    usernamesVector.Add(new TUsername
                    {
                        Username = doc["Username"].AsString,
                        Editable = doc.Contains("Editable") && doc["Editable"].AsBoolean,
                        Active = doc.Contains("Active") && doc["Active"].AsBoolean
                    });
                }
            }

            var updateUserName = new TUpdateUserName
            {
                UserId = userId,
                FirstName = firstName,
                LastName = lastName,
                Usernames = usernamesVector
            };

            // TODO: Send update to client via push updates
            // This requires access to ICommandBus or update sender service
        }

        return new TBoolTrue();
    }
}
