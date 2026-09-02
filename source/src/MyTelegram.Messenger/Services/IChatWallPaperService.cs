using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// The per-chat wallpaper set with <c>messages.setChatWallPaper</c>.
/// <para>
/// It is reported back as <c>userFull.wallpaper</c> / <c>chatFull.wallpaper</c> and kept fresh with
/// <c>updatePeerWallpaper</c>, see
/// <a href="https://corefork.telegram.org/api/wallpapers#installing-wallpapers-in-a-specific-chat-or-channel">wallpapers »</a>
/// and <a href="https://corefork.telegram.org/api/peers#handling-updates">peer database »</a>.
/// </para>
/// </summary>
public interface IChatWallPaperService
{
    /// <summary>
    /// The wallpaper <paramref name="ownerId"/> sees in the chat with <paramref name="peer"/>, and
    /// whether it was chosen by the other side with <c>for_both</c>. A channel wallpaper belongs to
    /// the channel itself, so it is stored and read with the channel as its own owner.
    /// </summary>
    Task<(MyTelegram.Schema.IWallPaper? WallPaper, bool Overridden)> GetChatWallPaperAsync(long ownerId, Peer peer);

    /// <summary>
    /// Stores the wallpaper. <paramref name="wallPaperId"/> of <c>null</c> removes it; <c>0</c> means a
    /// wallpaper that is nothing but its <paramref name="settings"/> — how a channel fill wallpaper
    /// arrives, see <see cref="ResolveWallPaperIdAsync"/>. The value being replaced is remembered so
    /// <c>revert</c> can put it back.
    /// </summary>
    Task SetChatWallPaperAsync(long ownerId, Peer peer, long? wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings, bool overridden);

    /// <summary>
    /// Puts back the wallpaper that was in place before the current one — "if the other user does not
    /// like the new wallpaper we have chosen for them, they can re-set their previous wallpaper just on
    /// their side, by invoking messages.setChatWallPaper, providing only the revert flag". Returns what
    /// is in place afterwards, which is nothing when there was no previous wallpaper.
    /// </summary>
    Task<MyTelegram.Schema.IWallPaper?> RevertChatWallPaperAsync(long ownerId, Peer peer);

    /// <summary>
    /// Resolves an <c>inputWallPaper</c> to its stored id: <c>null</c> when there is no wallpaper at all,
    /// <c>0</c> for <c>inputWallPaperNoFile{id = 0}</c>, which names no catalogue row and carries its
    /// whole identity in the settings — that is what a client sends for a channel wallpaper, together
    /// with <c>settings.emoticon</c>. Treating it as "no wallpaper", as this used to, removed the
    /// wallpaper the caller was trying to set.
    /// </summary>
    Task<long?> ResolveWallPaperIdAsync(MyTelegram.Schema.IInputWallPaper? inputWallPaper);

    /// <summary>
    /// Builds the wallpaper constructor, applying the per-chat <paramref name="settings"/> on top of
    /// the ones the wallpaper was uploaded with.
    /// </summary>
    Task<MyTelegram.Schema.IWallPaper?> GetWallPaperAsync(long wallPaperId,
        MyTelegram.Schema.IWallPaperSettings? settings);
}
