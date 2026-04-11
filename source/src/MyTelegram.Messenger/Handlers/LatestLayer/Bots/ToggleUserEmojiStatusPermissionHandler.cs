using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Allow or prevent a bot from <a href="https://corefork.telegram.org/api/emoji-status#setting-an-emoji-status-from-a-bot">changing our emoji status »</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.toggleUserEmojiStatusPermission"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleUserEmojiStatusPermissionHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestToggleUserEmojiStatusPermission, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestToggleUserEmojiStatusPermission obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_emoji_status_permissions");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", inputUser.UserId),
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)
        );

        var update = Builders<BsonDocument>.Update
            .Set("BotId", inputUser.UserId)
            .Set("UserId", input.UserId)
            .Set("Enabled", obj.Enabled)
            .Set("UpdatedAt", DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}