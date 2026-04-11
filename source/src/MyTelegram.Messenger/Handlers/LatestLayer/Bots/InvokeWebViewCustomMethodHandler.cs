namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Send a custom request from a <a href="https://corefork.telegram.org/api/bots/webapps">mini bot app</a>, triggered by a <a href="https://corefork.telegram.org/api/web-events#web-app-invoke-custom-method">web_app_invoke_custom_method event »</a>.The response should be sent using a <a href="https://corefork.telegram.org/api/bots/webapps#custom-method-invoked">custom_method_invoked</a> event, <a href="https://corefork.telegram.org/api/web-events#web-app-invoke-custom-method">see here »</a> for more info on the flow.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 DATA_JSON_INVALID The provided JSON data is invalid.
/// 400 METHOD_INVALID The specified method is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.invokeWebViewCustomMethod"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWebViewCustomMethodHandler(
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestInvokeWebViewCustomMethod, MyTelegram.Schema.IDataJSON>
{
    protected override async Task<MyTelegram.Schema.IDataJSON> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestInvokeWebViewCustomMethod obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        if (string.IsNullOrWhiteSpace(obj.CustomMethod))
            RpcErrors.RpcErrors400.MethodInvalid.ThrowRpcError();

        return new TDataJSON { Data = "{}" };
    }
}