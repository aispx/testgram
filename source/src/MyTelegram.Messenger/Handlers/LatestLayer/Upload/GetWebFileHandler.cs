using MyTelegram.Messenger.Services.WebFiles;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Returns the content of a web file — a file that lives on somebody else's server and is fetched
/// through this one.
///
/// <para>This is the read side of a proxied <c>webDocument</c>. It matters for GIF search: Telegram
/// clients only render an inline result whose media is a proxied <c>webDocument</c> — Android's
/// <c>ContextLinkCell</c> tests <c>instanceof TL_webDocument</c>, and <c>webDocumentNoProxy</c> is a
/// sibling class rather than a subclass of it, so a no-proxy result draws an empty tile forever.</para>
///
/// <para>Only a URL this server signed is served, and only from a host it is configured to proxy —
/// otherwise the method would be an open HTTP proxy into whatever the server can reach.</para>
/// See https://core.telegram.org/method/upload.getWebFile
/// </summary>
internal sealed class GetWebFileHandler(
    IWebDocumentUrlSigner urlSigner,
    IWebFileFetcher fetcher,
    ILogger<GetWebFileHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetWebFile, MyTelegram.Schema.Upload.IWebFile>
{
    /// <summary>Clients read a file in slices; this is the largest one that will be answered.</summary>
    private const int MaxLimit = 1024 * 1024;

    protected override async Task<MyTelegram.Schema.Upload.IWebFile> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Upload.RequestGetWebFile obj)
    {
        if (obj.Location is not TInputWebFileLocation location || string.IsNullOrEmpty(location.Url))
        {
            // Geo point and audio album cover locations are the other two variants, and neither is
            // produced anywhere in this server, so there is nothing to look up for them.
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
        }

        if (obj.Offset < 0)
        {
            RpcErrors.RpcErrors400.OffsetInvalid.ThrowRpcError();
        }

        if (obj.Limit is <= 0 or > MaxLimit)
        {
            RpcErrors.RpcErrors400.LimitInvalid.ThrowRpcError();
        }

        var url = ((TInputWebFileLocation)obj.Location).Url;

        if (!urlSigner.IsSignatureValid(url, ((TInputWebFileLocation)obj.Location).AccessHash))
        {
            logger.LogWarning("User {UserId} asked for a web file with a hash this server did not issue",
                input.UserId);
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
        }

        var body = await fetcher.GetAsync(url, null);
        if (body == null)
        {
            RpcErrors.RpcErrors400.WebfileNotAvailable.ThrowRpcError();
        }

        var bytes = body!.Bytes;
        var offset = Math.Min(obj.Offset, bytes.Length);
        var length = Math.Min(obj.Limit, bytes.Length - offset);

        return new MyTelegram.Schema.Upload.TWebFile
        {
            // The full size, not the slice: clients use it to know how much is left to read.
            Size = bytes.Length,
            MimeType = body.MimeType,
            FileType = WebFileTypeMapper.Map(body.MimeType),
            Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = new ReadOnlyMemory<byte>(bytes, offset, length)
        };
    }
}

/// <summary>
/// Maps a mime type onto the <c>storage.FileType</c> a client is told to expect. Clients pick a decoder
/// from it, so a wrong answer here shows up as a preview that never renders.
/// </summary>
internal static class WebFileTypeMapper
{
    public static MyTelegram.Schema.Storage.IFileType Map(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "video/mp4" => new MyTelegram.Schema.Storage.TFileMp4(),
            "image/gif" => new MyTelegram.Schema.Storage.TFileGif(),
            "image/png" => new MyTelegram.Schema.Storage.TFilePng(),
            "image/jpeg" or "image/jpg" => new MyTelegram.Schema.Storage.TFileJpeg(),
            "image/webp" => new MyTelegram.Schema.Storage.TFileWebp(),
            "video/quicktime" => new MyTelegram.Schema.Storage.TFileMov(),
            "audio/mpeg" or "audio/mp3" => new MyTelegram.Schema.Storage.TFileMp3(),
            "application/pdf" => new MyTelegram.Schema.Storage.TFilePdf(),
            _ => new MyTelegram.Schema.Storage.TFileUnknown()
        };
    }
}
