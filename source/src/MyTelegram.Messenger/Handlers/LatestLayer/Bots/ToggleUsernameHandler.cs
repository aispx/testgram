using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Activate or deactivate a purchased <a href="https://fragment.com/">fragment.com</a> username associated to a bot we own.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.toggleUsername"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleUsernameHandler(IMongoDatabase mongoDatabase, IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestToggleUsername, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestToggleUsername obj)
    {
        // Get bot user ID
        long botUserId;
        if (obj.Bot is TInputUser inputUser)
        {
            botUserId = inputUser.UserId;
        }
        else
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        // Check if user is a bot and we own it
        var botOwnersCol = mongoDatabase.GetCollection<BsonDocument>("bot-owners");
        var ownerDoc = await botOwnersCol.Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId)).FirstOrDefaultAsync();

        if (ownerDoc == null || ownerDoc["OwnerId"].AsInt64 != input.UserId)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        // Get bot's usernames
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", botUserId);
        var userDoc = await userCollection.Find(userFilter).FirstOrDefaultAsync();

        if (userDoc == null || !userDoc.GetValue("Bot", false).AsBoolean)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var usernames = userDoc.Contains("Usernames") && !userDoc["Usernames"].IsBsonNull
            ? userDoc["Usernames"].AsBsonArray.Select(x => x.AsString).ToList()
            : new List<string>();

        var username = obj.Username.ToLowerInvariant();

        // Check if username exists in the list
        if (!usernames.Contains(username))
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
        }

        var currentUsername = userDoc.GetValue("UserName", "").AsString;
        var isCurrentlyActive = currentUsername == username;

        if (obj.Active == isCurrentlyActive)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
        }

        // Toggle username
        if (obj.Active)
        {
            // Activate: set as primary username
            var update = Builders<BsonDocument>.Update.Set("UserName", username);
            await userCollection.UpdateOneAsync(userFilter, update);
        }
        else
        {
            // Deactivate: if it's the primary username, clear it
            if (isCurrentlyActive)
            {
                var update = Builders<BsonDocument>.Update.Set("UserName", "");
                await userCollection.UpdateOneAsync(userFilter, update);
            }
        }

        return new TBoolTrue();
    }
}