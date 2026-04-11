using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Change the emoji status of a user (invoked by bots, see <a href="https://corefork.telegram.org/api/emoji-status#setting-an-emoji-status-from-a-bot">here »</a> for more info on the full flow)
/// Possible errors
/// Code Type Description
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 403 USER_PERMISSION_DENIED The user hasn't granted or has revoked the bot's access to change their emoji status using <a href="https://corefork.telegram.org/method/bots.toggleUserEmojiStatusPermission">bots.toggleUserEmojiStatusPermission</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.updateUserEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateUserEmojiStatusHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (!(obj.UserId is TInputUser))
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.UserId;
        var targetUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (targetUser == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        var permissionCol = mongoDatabase.GetCollection<BsonDocument>("bot_emoji_status_permissions");
        var permissionFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("UserId", inputUser.UserId)
        );

        var permissionDoc = await permissionCol.Find(permissionFilter).FirstOrDefaultAsync();
        if (permissionDoc == null || !permissionDoc.Contains("Enabled") || !permissionDoc["Enabled"].AsBoolean)
            RpcErrors.RpcErrors403.UserPermissionDenied.ThrowRpcError();

        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", inputUser.UserId);

        BsonValue emojiStatusValue;
        if (obj.EmojiStatus is TEmojiStatus emojiStatus)
        {
            emojiStatusValue = new BsonDocument
            {
                ["Type"] = "TEmojiStatus",
                ["DocumentId"] = emojiStatus.DocumentId,
                ["Until"] = emojiStatus.Until.HasValue ? (BsonValue)emojiStatus.Until.Value : BsonNull.Value
            };
        }
        else
        {
            emojiStatusValue = BsonNull.Value;
        }

        var update = Builders<BsonDocument>.Update.Set("EmojiStatus", emojiStatusValue);
        await userCol.UpdateOneAsync(userFilter, update);

        return new TBoolTrue();
    }
}
