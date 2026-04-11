using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Edit a <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">main mini app preview, see here »</a> for more info.Only owners of bots with a configured Main Mini App can use this method, see <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">see here »</a> for more info on how to check if you can invoke this method.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.editPreviewMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditPreviewMediaHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestEditPreviewMedia, MyTelegram.Schema.IBotPreviewMedia>
{
    protected override async Task<MyTelegram.Schema.IBotPreviewMedia> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestEditPreviewMedia obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_preview_media");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotId", inputUser.UserId),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        var update = Builders<BsonDocument>.Update
            .Set("Media", obj.Media.GetType().Name)
            .Set("UpdatedAt", DateTime.UtcNow);

        await collection.UpdateOneAsync(filter, update);

        return new TBotPreviewMedia
        {
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Media = new TMessageMediaEmpty()
        };
    }
}