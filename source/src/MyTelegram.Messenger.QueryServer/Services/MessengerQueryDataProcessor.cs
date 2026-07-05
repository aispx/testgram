namespace MyTelegram.Messenger.QueryServer.Services;

public sealed class MessengerQueryDataProcessor : DefaultDataProcessor<MessengerQueryDataReceivedEvent>
{
    private readonly ILogger<MessengerQueryDataProcessor> _queryLogger;

    public MessengerQueryDataProcessor(
        IHandlerHelper handlerHelper,
        IObjectMessageSender objectMessageSender,
        ILogger<DefaultDataProcessor<MessengerQueryDataReceivedEvent>> logger,
        IExceptionProcessor exceptionProcessor,
        IRequestHelper requestHelper,
        IInvokeAfterMsgProcessor invokeAfterMsgProcessor,
        ILogger<MessengerQueryDataProcessor> queryLogger)
        : base(
            handlerHelper,
            objectMessageSender,
            logger,
            exceptionProcessor,
            requestHelper,
            invokeAfterMsgProcessor)
    {
        _queryLogger = queryLogger;
    }

    protected override Task SendMessageToPeerAsync(RequestInfo requestInfo, IObject data)
    {
        var uploadFile = data switch
        {
            MyTelegram.Schema.Upload.IFile file => file,
            TRpcResult { Result: MyTelegram.Schema.Upload.IFile file } => file,
            _ => null
        };
        var isUploadFile = uploadFile is not null;
        _queryLogger.LogDebug(
            "Messenger query response dispatch: reqMsgId: {ReqMsgId}, type: {Type}, isUploadFile: {IsUploadFile}",
            requestInfo.ReqMsgId,
            data.GetType().FullName,
            isUploadFile);

        if (uploadFile is not null)
        {
            _queryLogger.LogInformation(
                "Sending upload.getFile response as rpc_result, reqMsgId: {ReqMsgId}, bytes: {Bytes}",
                requestInfo.ReqMsgId,
                GetByteCount(uploadFile));

            return base.SendMessageToPeerAsync(requestInfo, data);
        }

        return base.SendMessageToPeerAsync(requestInfo, data);
    }

    private static int GetByteCount(IObject data)
    {
        return data is MyTelegram.Schema.Upload.TFile file ? file.Bytes.Length : 0;
    }
}
