namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Get SHA256 hashes for verifying downloaded CDN files
/// See https://core.telegram.org/method/upload.getCdnFileHashes
/// </summary>
internal sealed class GetCdnFileHashesHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetCdnFileHashes, TVector<MyTelegram.Schema.IFileHash>>
{
    private readonly ILogger<GetCdnFileHashesHandler> _logger;

    public GetCdnFileHashesHandler(ILogger<GetCdnFileHashesHandler> logger)
    {
        _logger = logger;
    }

    protected override Task<TVector<MyTelegram.Schema.IFileHash>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetCdnFileHashes obj)
    {
        // CDN infrastructure not implemented
        // Return empty hashes

        _logger.LogWarning("GetCdnFileHashes called but CDN not implemented. FileToken: {FileToken}", Convert.ToBase64String(obj.FileToken.ToArray()));

        return Task.FromResult(new TVector<MyTelegram.Schema.IFileHash>());
    }
}
