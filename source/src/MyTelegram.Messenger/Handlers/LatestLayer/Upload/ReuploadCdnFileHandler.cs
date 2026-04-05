namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Request a reupload of a certain file to a CDN DC.
/// See https://core.telegram.org/method/upload.reuploadCdnFile
/// </summary>
internal sealed class ReuploadCdnFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestReuploadCdnFile, TVector<MyTelegram.Schema.IFileHash>>
{
    private readonly ILogger<ReuploadCdnFileHandler> _logger;

    public ReuploadCdnFileHandler(ILogger<ReuploadCdnFileHandler> logger)
    {
        _logger = logger;
    }

    protected override Task<TVector<MyTelegram.Schema.IFileHash>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestReuploadCdnFile obj)
    {
        // CDN infrastructure not implemented
        // Return empty hashes (client will fallback to regular download)

        _logger.LogWarning("ReuploadCdnFile called but CDN not implemented. FileToken: {FileToken}", Convert.ToBase64String(obj.FileToken.ToArray()));

        return Task.FromResult(new TVector<MyTelegram.Schema.IFileHash>());
    }
}
