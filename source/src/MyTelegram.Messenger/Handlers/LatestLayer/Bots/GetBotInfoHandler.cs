using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Get localized name, about text and description of a bot (or of the current account, if called by a bot).
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 LANG_CODE_INVALID The specified language code is invalid.
/// 400 USER_BOT_INVALID User accounts must provide the <code>bot</code> method parameter when calling this method. If there is no such method parameter, this method can only be invoked by bot accounts.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getBotInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotInfoHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetBotInfo, MyTelegram.Schema.Bots.IBotInfo>
{
    protected override async Task<MyTelegram.Schema.Bots.IBotInfo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetBotInfo obj)
    {
        if (!string.IsNullOrEmpty(obj.LangCode) && obj.LangCode.Length > 10)
            RpcErrors.RpcErrors400.LangCodeInvalid.ThrowRpcError();

        long targetBotId;

        if (obj.Bot != null)
        {
            if (obj.Bot is TInputUser inputUser)
            {
                targetBotId = inputUser.UserId;
                var targetBot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(targetBotId));
                if (targetBot == null || !targetBot.Bot)
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

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_info");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", targetBotId),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        return new MyTelegram.Schema.Bots.TBotInfo
        {
            Name = doc?.Contains("Name") == true ? doc["Name"].AsString : string.Empty,
            About = doc?.Contains("About") == true ? doc["About"].AsString : string.Empty,
            Description = doc?.Contains("Description") == true ? doc["Description"].AsString : string.Empty
        };
    }
}