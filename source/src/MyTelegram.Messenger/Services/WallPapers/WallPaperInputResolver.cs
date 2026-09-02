namespace MyTelegram.Messenger.Services.WallPapers;

/// <summary>
/// Turns an <c>InputWallPaper</c> into a catalogue row.
///
/// <para>What <c>account.saveWallPaper</c> and <c>account.installWallPaper</c> must refuse is a wallpaper
/// the server does not have — "fill wallpapers cannot be saved to the server … clients should install and
/// keep track of them only locally". A client generates one of those with <c>id = 0</c>, and both methods
/// used to accept it and write a list entry pointing at nothing.</para>
///
/// <para>The <b>constructor</b> is not the test, though: Android removes a preinstalled fill wallpaper
/// from the list by sending <c>inputWallPaperNoFile{id}</c> to <c>saveWallPaper</c> with <c>unsave</c>
/// (<c>WallpapersListActivity</c>), which is the documented "including preinstalled wallpapers" case. So
/// what matters is whether the id names a row here, not which of the three constructors carried it.</para>
/// </summary>
internal static class WallPaperInputResolver
{
    /// <summary>
    /// The row named by the input. Throws <c>WALLPAPER_INVALID</c> — the only error either method
    /// documents — when the input names nothing this server holds.
    /// </summary>
    public static async Task<WallPaperRow> ResolveInstallableAsync(IWallPaperCatalog catalog,
        MyTelegram.Schema.IInputWallPaper? inputWallPaper)
    {
        var row = await ResolveAsync(catalog, inputWallPaper);

        if (row == null)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        return row!;
    }

    /// <summary>
    /// The row named by an input that may be any wallpaper, for the read methods. Returns null when the
    /// input names nothing this server holds, leaving the error to the caller — <c>getWallPaper</c> and
    /// <c>getMultiWallPapers</c> answer <c>WALLPAPER_INVALID</c>, <c>setChatWallPaper</c> answers
    /// <c>WALLPAPER_NOT_FOUND</c>.
    /// </summary>
    public static Task<WallPaperRow?> ResolveAsync(IWallPaperCatalog catalog,
        MyTelegram.Schema.IInputWallPaper? inputWallPaper)
    {
        return inputWallPaper switch
        {
            MyTelegram.Schema.TInputWallPaper { Id: not 0 } byId => catalog.FindByIdAsync(byId.Id),
            MyTelegram.Schema.TInputWallPaperNoFile { Id: not 0 } noFile => catalog.FindByIdAsync(noFile.Id),
            MyTelegram.Schema.TInputWallPaperSlug { Slug.Length: > 0 } bySlug => catalog.FindBySlugAsync(bySlug.Slug),
            _ => Task.FromResult<WallPaperRow?>(null)
        };
    }
}
