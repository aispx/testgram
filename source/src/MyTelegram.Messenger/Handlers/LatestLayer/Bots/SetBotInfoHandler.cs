using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Set localized name, about text and description of a bot (or of the current account, if called by a bot).
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 USER_BOT_INVALID User accounts must provide the <code>bot</code> method parameter when calling this method. If there is no such method parameter, this method can only be invoked by bot accounts.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setBotInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotInfoHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetBotInfo, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetBotInfo obj)
    {
        long targetBotId;

        if (obj.Bot != null)
        {
            if (obj.Bot is TInputUser inputUser)
            {
                targetBotId = inputUser.UserId;
                var targetBot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(targetBotId));
                if (targetBot == null || !targetBot.Bot)
                    RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

                // Being a bot is not enough: without an ownership check any user could rewrite
                // the public name/about/description of any bot on the server.
                if (targetBotId != input.UserId && !await IsBotOwnerAsync(targetBotId, input.UserId))
                    RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            }
            else
            {
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
                return null!;
            }
        }
        else
        {
            var currentUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
            if (currentUser == null || !currentUser.Bot)
                RpcErrors.RpcErrors400.UserBotInvalid.ThrowRpcError();
            targetBotId = input.UserId;
        }

        if (!string.IsNullOrEmpty(obj.LangCode) && obj.LangCode.Length > 10)
            RpcErrors.RpcErrors400.LangCodeInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_info");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", targetBotId),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        var update = Builders<BsonDocument>.Update
            .Set("BotId", targetBotId)
            .Set("LangCode", obj.LangCode ?? "")
            .Set("UpdatedAt", DateTime.UtcNow);

        if (obj.Name != null)
            update = update.Set("Name", obj.Name);

        if (obj.About != null)
            update = update.Set("About", obj.About);

        if (obj.Description != null)
            update = update.Set("Description", obj.Description);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }

    /// <summary>
    /// A bot is owned by the user recorded in <c>bot-owners</c>, or by the <c>CreatorUserId</c> on the
    /// bot's user read model — both are written depending on how the bot was registered.
    /// </summary>
    private async Task<bool> IsBotOwnerAsync(long botUserId, long ownerUserId)
    {
        var ownedViaBotOwners = await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("OwnerId", ownerUserId))
            .Limit(1)
            .AnyAsync();
        if (ownedViaBotOwners)
            return true;

        return await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("CreatorUserId", ownerUserId))
            .Limit(1)
            .AnyAsync();
    }
}