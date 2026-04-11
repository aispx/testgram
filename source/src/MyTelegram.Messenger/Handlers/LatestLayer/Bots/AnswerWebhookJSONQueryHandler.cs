namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Answers a custom query; for bots only
/// Possible errors
/// Code Type Description
/// 400 DATA_JSON_INVALID The provided JSON data is invalid.
/// 400 QUERY_ID_INVALID The query ID is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.answerWebhookJSONQuery"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class AnswerWebhookJSONQueryHandler(
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestAnswerWebhookJSONQuery, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestAnswerWebhookJSONQuery obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (obj.QueryId <= 0)
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();

        return new TBoolTrue();
    }
}