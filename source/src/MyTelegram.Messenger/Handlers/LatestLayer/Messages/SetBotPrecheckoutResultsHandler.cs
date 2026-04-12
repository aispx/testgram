using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Once the user has confirmed their payment and shipping details, the bot receives an <a href="https://corefork.telegram.org/constructor/updateBotPrecheckoutQuery">updateBotPrecheckoutQuery</a> update.<br/>
/// Use this method to respond to such pre-checkout queries.<br/>
/// <strong>Note</strong>: Telegram must receive an answer within 10 seconds after the pre-checkout query was sent.
/// Possible errors
/// Code Type Description
/// 400 ERROR_TEXT_EMPTY The provided error message is empty.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setBotPrecheckoutResults"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotPrecheckoutResultsHandler(
    IMongoDatabase mongoDatabase,
    ILogger<SetBotPrecheckoutResultsHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetBotPrecheckoutResults, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetBotPrecheckoutResults obj)
    {
        // Validate that caller is a bot
        var botUser = await mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-userreadmodel")
            .Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("UserId", input.UserId))
            .FirstOrDefaultAsync();

        if (botUser == null || !botUser.Contains("Bot") || !botUser["Bot"].AsBoolean)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        // Validate error message if not success
        if (!obj.Success && string.IsNullOrEmpty(obj.Error))
            RpcErrors.RpcErrors400.ErrorTextEmpty.ThrowRpcError();

        // Update pending precheckout query in MongoDB
        var collection = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("pending_precheckout_queries");
        var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.And(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("query_id", obj.QueryId),
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("bot_id", input.UserId)
        );

        var query = await collection.Find(filter).FirstOrDefaultAsync();
        if (query == null)
        {
            logger.LogWarning("Precheckout query not found: queryId={QueryId} botId={BotId}", obj.QueryId, input.UserId);
            return new TBoolFalse();
        }

        // Update query with bot's response
        var update = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update
            .Set("success", obj.Success)
            .Set("error", obj.Error ?? "")
            .Set("responded_at", DateTime.UtcNow.ToTimestamp());

        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Bot precheckout response: queryId={QueryId} botId={BotId} success={Success}",
            obj.QueryId, input.UserId, obj.Success);

        return new TBoolTrue();
    }
}