using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Reorder usernames associated to a bot we own.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.reorderUsernames"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReorderUsernamesHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestReorderUsernames, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestReorderUsernames obj)
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

        var currentUsernames = userDoc.Contains("Usernames") && !userDoc["Usernames"].IsBsonNull
            ? userDoc["Usernames"].AsBsonArray.Select(x => x.AsString).ToList()
            : new List<string>();

        var newOrder = obj.Order.Select(u => u.ToLowerInvariant()).ToList();

        // Validate that all usernames in new order exist in current usernames
        if (newOrder.Count != currentUsernames.Count || !newOrder.All(u => currentUsernames.Contains(u)))
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
        }

        // Check if order actually changed
        if (currentUsernames.SequenceEqual(newOrder))
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
        }

        // Update usernames order
        var update = Builders<BsonDocument>.Update.Set("Usernames", new BsonArray(newOrder));
        await userCollection.UpdateOneAsync(userFilter, update);

        return new TBoolTrue();
    }
}