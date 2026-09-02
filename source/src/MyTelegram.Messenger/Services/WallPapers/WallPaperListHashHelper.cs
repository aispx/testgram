namespace MyTelegram.Messenger.Services.WallPapers;

/// <summary>
/// The <c>hash</c> of <c>account.wallPapers</c>.
///
/// <para><b>This one is the client's, computed by the client.</b> Android folds its own cached list with
/// <c>acc = MediaDataController.calcHash(acc, wallPaper.id)</c> and sends the result
/// (<c>WallpapersListActivity.loadWallpapers</c>), so the server has to arrive at the same number from
/// the same ids in the same order or <c>wallPapersNotModified</c> can never fire. tdesktop quotes the
/// server's value straight back (<c>Session::setWallpapers</c> stores <c>data.vhash()</c> and
/// <c>wallpapersHash()</c> returns it), and tdlib always sends <c>0</c>
/// (<c>account_getWallPapers(0)</c>) — so Android is the one that constrains the algorithm, and the
/// other two are satisfied by any stable value.</para>
///
/// <para>It used to be <c>System.HashCode</c>, which is seeded randomly per process: the value changed
/// on every restart, never matched Android's, and was an <c>int</c> widened into the <c>long</c> the
/// field carries. The list was therefore re-downloaded on every poll, which logs nothing.</para>
///
/// <para>Android skips entries with <c>id &lt; 0</c> when folding, so a served wallpaper id must always
/// be positive — otherwise its hash is computed over a shorter list than the one it cached.</para>
/// </summary>
internal static class WallPaperListHashHelper
{
    public static long ComputeHash(IEnumerable<MyTelegram.Schema.IWallPaper> wallPapers)
    {
        return Hashing.VectorHashHelper.ComputeHash(wallPapers.Select(IdOf).Where(id => id >= 0));
    }

    private static long IdOf(MyTelegram.Schema.IWallPaper wallPaper)
    {
        return wallPaper switch
        {
            MyTelegram.Schema.TWallPaper paper => paper.Id,
            MyTelegram.Schema.TWallPaperNoFile noFile => noFile.Id,
            _ => 0
        };
    }
}
