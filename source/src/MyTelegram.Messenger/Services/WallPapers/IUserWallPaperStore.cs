namespace MyTelegram.Messenger.Services.WallPapers;

/// <summary>
/// The wallpaper list of one user — the answer to <c>account.getWallPapers</c>.
///
/// <para>The API keeps this list per account: it starts as the preinstalled set, <c>saveWallPaper</c>
/// adds to it, <c>saveWallPaper</c> with <c>unsave</c> removes from it <b>including a preinstalled
/// one</b>, and <c>resetWallPapers</c> restores it. <c>getWallPapers</c> used to answer with the whole
/// catalogue regardless of the caller and never read this collection at all, so removing a wallpaper
/// wrote a row nothing consulted and the wallpaper came straight back on the next poll.</para>
///
/// <para>See https://corefork.telegram.org/api/wallpapers#installing-wallpapers</para>
/// </summary>
public interface IUserWallPaperStore
{
    /// <summary>
    /// The list, in the order it must be served: the user's own saved wallpapers newest first, then the
    /// preinstalled ones they have not removed. The order is part of the contract — clients render the
    /// vector as it arrives and Android folds the list hash over it.
    /// </summary>
    Task<List<MyTelegram.Schema.IWallPaper>> GetListAsync(long userId);

    /// <summary>Adds the wallpaper to the list, or moves it to the front if it is already there.</summary>
    Task SaveAsync(long userId, WallPaperRow row, MyTelegram.Schema.IWallPaperSettings? settings);

    /// <summary>
    /// Removes the wallpaper from the list. A preinstalled one leaves a tombstone behind, because
    /// nothing else could keep it out of the list that is built from the catalogue.
    /// </summary>
    Task UnsaveAsync(long userId, WallPaperRow row);

    /// <summary>Whether the wallpaper is currently in the user's list.</summary>
    Task<bool> IsSavedAsync(long userId, long wallPaperId);

    /// <summary>
    /// Drops every saved wallpaper and every tombstone, which restores the preinstalled set —
    /// "removing all installed wallpapers and reinstalling previously removed preinstalled wallpapers".
    /// </summary>
    Task ResetAsync(long userId);
}
