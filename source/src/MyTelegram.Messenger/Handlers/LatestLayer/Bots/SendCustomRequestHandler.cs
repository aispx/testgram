namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Sends a custom request; for bots only
/// Possible errors
/// Code Type Description
/// 400 DATA_JSON_INVALID The provided JSON data is invalid.
/// 400 METHOD_INVALID The specified method is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.sendCustomRequest"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SendCustomRequestHandler(
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSendCustomRequest, MyTelegram.Schema.IDataJSON>
{
    protected override async Task<MyTelegram.Schema.IDataJSON> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestSendCustomRequest obj)
    {
        var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (userReadModel == null || !userReadModel.Bot)
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();

        if (string.IsNullOrWhiteSpace(obj.CustomMethod))
            RpcErrors.RpcErrors400.MethodInvalid.ThrowRpcError();

        return new TDataJSON { Data = "{}" };
    }
}