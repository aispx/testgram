using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Create and upload a new <a href="https://corefork.telegram.org/api/wallpapers">wallpaper</a>
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_FILE_INVALID The specified wallpaper file is invalid.
/// 400 WALLPAPER_MIME_INVALID The specified wallpaper MIME type is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.uploadWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>This used to answer with <c>documentEmpty</c> wrapped around the <b>upload</b> file id, and to
/// create no document at all. Every client treats that as a failure it cannot report: tdesktop logs
/// "Got wallPaperNoFile after account.UploadWallPaper" and applies nothing, Android has no thumbnail to
/// draw (<c>MessagesController.uploadWallpaper</c> reads <c>document.thumbs</c>), and
/// <c>account.getWallPapers</c> drops the row because the document it names does not exist.</para>
///
/// <para>The document therefore goes through <see cref="IMediaHelper.SaveMediaAsync"/> — the same gRPC
/// route <c>messages.sendMedia</c>, <c>messages.uploadMedia</c>, <c>photos.uploadProfilePhoto</c> and
/// <c>channels.editPhoto</c> use. The file server owns <c>eventflow-documentreadmodel</c> and writes the
/// row, so the wallpaper is downloadable and appears in the list the moment this returns.</para>
///
/// <para>Layer 224 defines this method as <c>#e39a8f03</c>, with a <c>for_chat</c> flag; the generated
/// request class here is the older <c>#dd853661</c>, which is what this fork's Android client sends. The
/// newer constructor is served by the <c>LayerN</c> forwarder, which drops the flag: Android uploads a
/// chat wallpaper <b>without</b> it (<c>MessagesController.uploadWallpaper</c> sets no flags) while
/// tdesktop always sets it, so requiring it would refuse what the official client does. Every upload is
/// therefore saved to the uploader's list, which is what Android assumes when it posts
/// <c>wallpapersNeedReload</c> after the call.</para>
/// </remarks>
internal sealed class UploadWallPaperHandler(IMediaHelper mediaHelper, IWallPaperCatalog catalog,
    IUserWallPaperStore userWallPaperStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUploadWallPaper, MyTelegram.Schema.IWallPaper>
{
    /// <summary>
    /// What a client may upload. <c>image/png</c> and the pattern mime are here because a pattern is a
    /// PNG or a TGV — "the PNG or TGV (gzipped subset of SVG with MIME type
    /// application/x-tgwallpattern) pattern image contained in the document field". tdlib uploads a
    /// pattern as <c>image/png</c> (<c>BackgroundType::get_mime_type</c>), Android always sends
    /// <c>image/jpeg</c>.
    /// </summary>
    private static readonly string[] AllowedMimeTypes =
    [
        "image/jpeg", "image/jpg", "image/png", "image/webp", "application/x-tgwallpattern"
    ];

    private const string PatternMimeType = "application/x-tgwallpattern";

    protected override async Task<MyTelegram.Schema.IWallPaper> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestUploadWallPaper obj)
    {
        var mimeType = (obj.MimeType ?? string.Empty).ToLowerInvariant();
        if (!AllowedMimeTypes.Contains(mimeType))
        {
            RpcErrors.RpcErrors400.WallpaperMimeInvalid.ThrowRpcError();
        }

        // Both upload routes are legal: a client switches to inputFileBig past 10 MB and a wallpaper is
        // easily that large. Accepting only inputFile made every large wallpaper WALLPAPER_FILE_INVALID.
        if (obj.File is not (MyTelegram.Schema.TInputFile or MyTelegram.Schema.TInputFileBig))
        {
            RpcErrors.RpcErrors400.WallpaperFileInvalid.ThrowRpcError();
        }

        var media = await mediaHelper.SaveMediaAsync(new MyTelegram.Schema.TInputMediaUploadedDocument
        {
            File = obj.File,
            MimeType = mimeType,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>()
        });

        if (media is not MyTelegram.Schema.TMessageMediaDocument { Document: MyTelegram.Schema.TDocument document })
        {
            RpcErrors.RpcErrors400.WallpaperFileInvalid.ThrowRpcError();

            return null!;
        }

        var settings = WallPaperSettingsHelper.PairSharedFlags(obj.Settings as MyTelegram.Schema.TWallPaperSettings);
        var row = await catalog.InsertUploadedAsync(input.UserId, document.Id, mimeType,
            IsPattern(mimeType, settings), forChat: false, settings);

        await userWallPaperStore.SaveAsync(input.UserId, row, settings);

        var wallPaper = await catalog.BuildAsync(row, input.UserId, settings);
        if (wallPaper == null)
        {
            RpcErrors.RpcErrors400.WallpaperFileInvalid.ThrowRpcError();
        }

        return wallPaper!;
    }

    /// <summary>
    /// A pattern is combined with the colour fill from the settings rather than drawn on its own, so a
    /// PNG that arrives with a background colour is one. The request carries no flag saying so, and
    /// nothing but the wallpaper's own <c>pattern</c> flag tells a client which of the two rendering paths
    /// to take — this rule is read off the clients, not measured against the official server.
    /// </summary>
    private static bool IsPattern(string mimeType, MyTelegram.Schema.TWallPaperSettings? settings)
    {
        return mimeType == PatternMimeType ||
               (mimeType == "image/png" && settings?.BackgroundColor is not null);
    }
}
