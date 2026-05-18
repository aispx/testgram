namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Returns content of a web file, by proxying the request through telegram.
/// See https://core.telegram.org/method/upload.getWebFile
/// </summary>
internal sealed class GetWebFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetWebFile, MyTelegram.Schema.Upload.IWebFile>
{
    private readonly ILogger<GetWebFileHandler> _logger;

    public GetWebFileHandler(ILogger<GetWebFileHandler> logger)
    {
        _logger = logger;
    }

    protected override Task<MyTelegram.Schema.Upload.IWebFile> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetWebFile obj)
    {
        _logger.LogWarning("GetWebFile called but web proxy not implemented. Location: {Location}", obj.Location);
        RpcErrors.RpcErrors400.WebfileNotAvailable.ThrowRpcError();
        throw new InvalidOperationException();
    }
}
