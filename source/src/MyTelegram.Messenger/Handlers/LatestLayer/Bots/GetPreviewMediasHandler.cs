using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">main mini app previews, see here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getPreviewMedias"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPreviewMediasHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetPreviewMedias, TVector<MyTelegram.Schema.IBotPreviewMedia>>
{
    protected override async Task<TVector<MyTelegram.Schema.IBotPreviewMedia>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetPreviewMedias obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_preview_media");
        var filter = Builders<BsonDocument>.Filter.Eq("BotId", inputUser.UserId);
        var docs = await collection.Find(filter).ToListAsync();

        var result = new TVector<IBotPreviewMedia>();
        foreach (var doc in docs)
        {
            result.Add(new TBotPreviewMedia
            {
                Date = doc.Contains("CreatedAt") ? (int)doc["CreatedAt"].ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds : 0,
                Media = new TMessageMediaEmpty()
            });
        }

        return result;
    }
}