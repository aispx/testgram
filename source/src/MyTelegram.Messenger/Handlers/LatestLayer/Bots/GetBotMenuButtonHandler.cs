using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Gets the menu button action for a given user or for all users, previously set using <a href="https://corefork.telegram.org/method/bots.setBotMenuButton">bots.setBotMenuButton</a>; users can see this information in the <a href="https://corefork.telegram.org/constructor/botInfo">botInfo</a> constructor.
/// Possible errors
/// Code Type Description
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getBotMenuButton"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotMenuButtonHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetBotMenuButton, MyTelegram.Schema.IBotMenuButton>
{
    protected override async Task<MyTelegram.Schema.IBotMenuButton> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetBotMenuButton obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        long? targetUserId = null;
        if (obj.UserId is TInputUser inputUser)
        {
            targetUserId = inputUser.UserId;
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_menu_buttons");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("UserId", targetUserId.HasValue ? (BsonValue)targetUserId.Value : BsonNull.Value)
        );

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        if (doc != null && doc.Contains("Button") && doc["Button"].IsBsonDocument)
        {
            var buttonDoc = doc["Button"].AsBsonDocument;
            var buttonType = buttonDoc["Type"].AsString;

            if (buttonType == "TBotMenuButton")
            {
                return new TBotMenuButton
                {
                    Text = buttonDoc["Text"].AsString,
                    Url = buttonDoc["Url"].AsString
                };
            }
            else if (buttonType == "TBotMenuButtonCommands")
            {
                return new TBotMenuButtonCommands();
            }
        }

        return new TBotMenuButtonDefault();
    }
}