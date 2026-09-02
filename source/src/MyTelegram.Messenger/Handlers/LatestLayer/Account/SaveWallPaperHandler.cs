using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Install/uninstall wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Removing a <b>preinstalled</b> wallpaper is part of the contract — "To remove a wallpaper
/// (including preinstalled wallpapers) from the list use account.saveWallPaper with unsave=true" — and it
/// is why the store keeps a tombstone: the list is otherwise rebuilt from the catalogue on every
/// call.</para>
/// </remarks>
internal sealed class SaveWallPaperHandler(IWallPaperCatalog catalog, IUserWallPaperStore userWallPaperStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveWallPaper, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestSaveWallPaper obj)
    {
        var row = await WallPaperInputResolver.ResolveInstallableAsync(catalog, obj.Wallpaper);

        if (obj.Unsave)
        {
            await userWallPaperStore.UnsaveAsync(input.UserId, row);
        }
        else
        {
            // The settings travel with the entry: blur and motion are the user's choice and have to be the
            // same on their next device, which is the only reason this method takes them at all.
            await userWallPaperStore.SaveAsync(input.UserId, row, obj.Settings);
        }

        return new TBoolTrue();
    }
}
