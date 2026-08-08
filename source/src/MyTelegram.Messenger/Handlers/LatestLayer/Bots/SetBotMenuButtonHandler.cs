using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Sets the <a href="https://corefork.telegram.org/api/bots/menu">menu button action »</a> for a given user or for all users
/// Possible errors
/// Code Type Description
/// 400 BUTTON_INVALID The specified button is invalid.
/// 400 BUTTON_TEXT_INVALID The specified button text is invalid.
/// 400 BUTTON_URL_INVALID Button URL invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setBotMenuButton"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetBotMenuButtonHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetBotMenuButton, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSetBotMenuButton obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        long? targetUserId = null;
        if (obj.UserId is TInputUser inputUser)
        {
            targetUserId = inputUser.UserId;
        }

        if (obj.Button is TBotMenuButton menuButton)
        {
            if (string.IsNullOrWhiteSpace(menuButton.Text) || menuButton.Text.Length > 64)
                RpcErrors.RpcErrors400.ButtonTextInvalid.ThrowRpcError();

            if (string.IsNullOrWhiteSpace(menuButton.Url) || menuButton.Url.Length > 1024)
                RpcErrors.RpcErrors400.ButtonUrlInvalid.ThrowRpcError();

            // Clients open this url directly, so restrict it to https the same way the webview
            // handlers do — otherwise javascript:/data: urls can be stored and surfaced.
            if (!Uri.TryCreate(menuButton.Url, UriKind.Absolute, out var menuButtonUri) ||
                menuButtonUri.Scheme != Uri.UriSchemeHttps)
                RpcErrors.RpcErrors400.ButtonUrlInvalid.ThrowRpcError();
        }

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_menu_buttons");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("UserId", targetUserId.HasValue ? (BsonValue)targetUserId.Value : BsonNull.Value)
        );

        BsonDocument buttonDoc;
        if (obj.Button is TBotMenuButton btn)
        {
            buttonDoc = new BsonDocument
            {
                ["Type"] = "TBotMenuButton",
                ["Text"] = btn.Text,
                ["Url"] = btn.Url
            };
        }
        else if (obj.Button is TBotMenuButtonCommands)
        {
            buttonDoc = new BsonDocument { ["Type"] = "TBotMenuButtonCommands" };
        }
        else
        {
            buttonDoc = new BsonDocument { ["Type"] = "TBotMenuButtonDefault" };
        }

        var update = Builders<BsonDocument>.Update
            .Set("BotId", input.UserId)
            .Set("UserId", targetUserId.HasValue ? (BsonValue)targetUserId.Value : BsonNull.Value)
            .Set("Button", buttonDoc)
            .Set("UpdatedAt", DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}