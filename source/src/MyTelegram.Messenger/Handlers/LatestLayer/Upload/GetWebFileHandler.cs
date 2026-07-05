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
        _logger.LogInformation(
            "GetWebFile fallback returned empty payload. Location: {Location}, Offset: {Offset}, Limit: {Limit}",
            obj.Location,
            obj.Offset,
            obj.Limit);

        return Task.FromResult<MyTelegram.Schema.Upload.IWebFile>(new MyTelegram.Schema.Upload.TWebFile
        {
            Size = 0,
            MimeType = "application/octet-stream",
            FileType = new MyTelegram.Schema.Storage.TFileUnknown(),
            Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = ReadOnlyMemory<byte>.Empty
        });
    }
}
