using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Add a <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">main mini app preview, see here »</a> for more info.Only owners of bots with a configured Main Mini App can use this method, see <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">see here »</a> for more info on how to check if you can invoke this method.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.addPreviewMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AddPreviewMediaHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestAddPreviewMedia, MyTelegram.Schema.IBotPreviewMedia>
{
    protected override async Task<MyTelegram.Schema.IBotPreviewMedia> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestAddPreviewMedia obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_preview_media");

        var countersCol = mongoDatabase.GetCollection<BsonDocument>("counters");
        var counterFilter = Builders<BsonDocument>.Filter.Eq("_id", "bot_preview_media_id");
        var counterUpdate = Builders<BsonDocument>.Update.Inc("seq", 1);
        var counterOptions = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var counterResult = await countersCol.FindOneAndUpdateAsync(counterFilter, counterUpdate, counterOptions);
        var mediaId = counterResult["seq"].AsInt64;

        var doc = new BsonDocument
        {
            ["_id"] = $"bot-preview-media-{mediaId}",
            ["MediaId"] = mediaId,
            ["BotId"] = inputUser.UserId,
            ["LangCode"] = obj.LangCode ?? "",
            ["Media"] = obj.Media.GetType().Name,
            ["CreatedAt"] = DateTime.UtcNow
        };

        await collection.InsertOneAsync(doc);

        return new TBotPreviewMedia
        {
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Media = new TMessageMediaEmpty()
        };
    }
}