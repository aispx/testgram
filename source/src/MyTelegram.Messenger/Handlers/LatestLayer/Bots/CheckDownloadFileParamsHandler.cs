using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Check if a <a href="https://corefork.telegram.org/api/bots/webapps">mini app</a> can request the download of a specific file: called when handling <a href="https://corefork.telegram.org/api/web-events#web-app-request-file-download">web_app_request_file_download events »</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.checkDownloadFileParams"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckDownloadFileParamsHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestCheckDownloadFileParams, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestCheckDownloadFileParams obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        return new TBoolTrue();
    }
}