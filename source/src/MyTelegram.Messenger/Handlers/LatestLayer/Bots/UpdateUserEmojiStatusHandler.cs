using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Change the emoji status of a user (invoked by bots, see <a href="https://corefork.telegram.org/api/emoji-status#setting-an-emoji-status-from-a-bot">here for more info on the full flow</a>)
/// Possible errors
/// Code Type Description
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 403 USER_PERMISSION_DENIED The user hasn't granted or has revoked the bot's access to change their emoji status.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.updateUserEmojiStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateUserEmojiStatusHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    MyTelegram.Services.Services.IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestUpdateUserEmojiStatus obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (obj.UserId is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

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

        long? documentId = null;
        int? until = null;
        long? collectibleId = null;

        switch (obj.EmojiStatus)
        {
            case TEmojiStatus emojiStatus:
                documentId = emojiStatus.DocumentId;
                until = emojiStatus.Until;
                break;
            case TEmojiStatusEmpty:
                break;
            case TInputEmojiStatusCollectible collectible:
            {
                var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .Find(d => d.OwnerUserId == inputUser.UserId && d.UniqueId == collectible.CollectibleId && !d.Burned)
                    .FirstOrDefaultAsync();
                if (doc == null)
                    RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
                documentId = model?.DocumentId ?? doc.DocumentId;
                collectibleId = collectible.CollectibleId;
                until = collectible.Until;
                break;
            }
            default:
                RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
                break;
        }

        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", inputUser.UserId);

        BsonValue emojiStatusDocumentValue = documentId.HasValue ? new BsonInt64(documentId.Value) : BsonNull.Value;
        BsonValue emojiStatusUntilValue = until.HasValue ? new BsonInt32(until.Value) : BsonNull.Value;
        var update = Builders<BsonDocument>.Update
            .Set("EmojiStatusDocumentId", emojiStatusDocumentValue)
            .Set("EmojiStatusValidUntil", emojiStatusUntilValue)
            .Set("EmojiStatusCollectibleId", (BsonValue)(collectibleId.HasValue ? new BsonInt64(collectibleId.Value) : BsonNull.Value));
        if (documentId.HasValue)
        {
            update = update.PushEach("RecentEmojiStatuses", [documentId.Value], slice: -10);
        }
        await userCol.UpdateOneAsync(userFilter, update);

        IEmojiStatus resolvedStatus = documentId.HasValue
            ? new TEmojiStatus { DocumentId = documentId.Value, Until = until }
            : new TEmojiStatusEmpty();

        var emojiUpdate = new TUpdateUserEmojiStatus
        {
            UserId = inputUser.UserId,
            EmojiStatus = resolvedStatus
        };
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(emojiUpdate),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, inputUser.UserId),
            updates);

        return new TBoolTrue();
    }
}
