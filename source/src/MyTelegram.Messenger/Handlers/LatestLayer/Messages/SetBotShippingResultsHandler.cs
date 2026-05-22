using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// If you sent an invoice requesting a shipping address and the parameter is_flexible was specified, the bot will receive an <a href="https://corefork.telegram.org/constructor/updateBotShippingQuery">updateBotShippingQuery</a> update. Use this method to reply to shipping queries.
/// Possible errors
/// Code Type Description
/// 400 QUERY_ID_INVALID The query ID is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setBotShippingResults"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotShippingResultsHandler(
    IMongoDatabase mongoDatabase,
    ILogger<SetBotShippingResultsHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetBotShippingResults, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetBotShippingResults obj)
    {
        var botUser = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", input.UserId))
            .FirstOrDefaultAsync();

        if (botUser == null || !botUser.Contains("Bot") || !botUser["Bot"].AsBoolean)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.Error != null && string.IsNullOrWhiteSpace(obj.Error))
        {
            RpcErrors.RpcErrors400.ErrorTextEmpty.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>("pending_shipping_queries");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("query_id", obj.QueryId),
            Builders<BsonDocument>.Filter.Eq("bot_id", input.UserId));

        var query = await collection.Find(filter).FirstOrDefaultAsync();
        if (query == null)
        {
            logger.LogWarning("Shipping query not found: queryId={QueryId} botId={BotId}", obj.QueryId, input.UserId);
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        var update = Builders<BsonDocument>.Update
            .Set("success", obj.Error == null)
            .Set("error", obj.Error ?? string.Empty)
            .Set("shipping_options", obj.ShippingOptions?.ToBytes() ?? Array.Empty<byte>())
            .Set("responded_at", DateTime.UtcNow.ToTimestamp());

        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Bot shipping response: queryId={QueryId} botId={BotId} success={Success}",
            obj.QueryId, input.UserId, obj.Error == null);

        return new TBoolTrue();
    }
}
