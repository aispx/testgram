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
        // GetWebFile is used for downloading web files through Telegram proxy
        // This requires HTTP proxy infrastructure which is not implemented
        // Return empty web file for now

        _logger.LogWarning("GetWebFile called but web proxy not implemented. Location: {Location}", obj.Location);

        var result = new MyTelegram.Schema.Upload.TWebFile
        {
            Size = 0,
            MimeType = "application/octet-stream",
            FileType = new TStorage.TFileUnknown(),
            Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = Array.Empty<byte>()
        };

        return Task.FromResult<MyTelegram.Schema.Upload.IWebFile>(result);
    }
}
