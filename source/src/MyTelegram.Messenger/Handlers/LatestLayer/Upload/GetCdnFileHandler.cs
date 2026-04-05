namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Download a CDN file.
/// See https://core.telegram.org/method/upload.getCdnFile
/// </summary>
internal sealed class GetCdnFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetCdnFile, MyTelegram.Schema.Upload.ICdnFile>
{
    private readonly ILogger<GetCdnFileHandler> _logger;

    public GetCdnFileHandler(ILogger<GetCdnFileHandler> logger)
    {
        _logger = logger;
    }

    protected override Task<MyTelegram.Schema.Upload.ICdnFile> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetCdnFile obj)
    {
        // CDN infrastructure not implemented
        // Return redirect to master DC (this server)

        _logger.LogWarning("GetCdnFile called but CDN not implemented. FileToken: {FileToken}", Convert.ToBase64String(obj.FileToken.ToArray()));

        // Return CdnFileReuploadNeeded to tell client to use regular upload.getFile instead
        var result = new MyTelegram.Schema.Upload.TCdnFileReuploadNeeded
        {
            RequestToken = obj.FileToken.ToArray()
        };

        return Task.FromResult<MyTelegram.Schema.Upload.ICdnFile>(result);
    }
}
