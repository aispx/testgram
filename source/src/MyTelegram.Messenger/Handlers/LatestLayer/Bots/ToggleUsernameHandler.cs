using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Activate or deactivate a purchased fragment.com username for a bot
/// See https://core.telegram.org/method/bots.toggleUsername
/// </summary>
internal sealed class ToggleUsernameHandler : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestToggleUsername, IBool>
{
    private readonly IMongoDatabase _database;

    public ToggleUsernameHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Bots.RequestToggleUsername obj)
    {
        if (obj.Bot is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var botUserId = inputUser.UserId;

        if (string.IsNullOrEmpty(obj.Username))
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
            return null!;
        }

        var username = obj.Username.ToLower();

        // Get bot from MongoDB
        var userCollection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var botFilter = Builders<BsonDocument>.Filter.Eq("UserId", botUserId);
        var bot = await userCollection.Find(botFilter).FirstOrDefaultAsync();

        if (bot == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        // Check if it's a bot
        if (!bot.Contains("Bot") || !bot["Bot"].AsBoolean)
        {
            RpcErrors.RpcErrors400.UserBotInvalid.ThrowRpcError();
            return null!;
        }

        // Only the bot's owner may toggle its usernames; otherwise any user could deactivate
        // another bot's username and make it unreachable by @name.
        if (!await IsBotOwnerAsync(botUserId, input.UserId))
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        // Get current usernames
        var usernamesV2 = bot.Contains("Usernames") && !bot["Usernames"].IsBsonNull
            ? bot["Usernames"].AsBsonArray
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

        // Check if it's editable
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
        var update = Builders<BsonDocument>.Update.Set("Usernames", usernamesV2);
        await userCollection.UpdateOneAsync(botFilter, update);

        return new TBoolTrue();
    }

    private async Task<bool> IsBotOwnerAsync(long botUserId, long ownerUserId)
    {
        if (botUserId == ownerUserId)
            return true;

        var ownedViaBotOwners = await _database.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .AnyAsync();
        if (ownedViaBotOwners)
            return true;

        return await _database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("CreatorUserId", ownerUserId))
            .Limit(1)
            .AnyAsync();
    }
}
