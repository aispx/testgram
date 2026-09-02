using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Get info about a certain wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetWallPaperHandler(IWallPaperCatalog catalog)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetWallPaper, MyTelegram.Schema.IWallPaper>
{
    protected override async Task<MyTelegram.Schema.IWallPaper> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetWallPaper obj)
    {
        var row = await WallPaperInputResolver.ResolveAsync(catalog, obj.Wallpaper);
        if (row == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        // Null means the row names a document that no longer exists, which is the same thing to a client
        // as the wallpaper not being here.
        var wallPaper = await catalog.BuildAsync(row!, input.UserId);
        if (wallPaper == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        return wallPaper!;
    }
}
