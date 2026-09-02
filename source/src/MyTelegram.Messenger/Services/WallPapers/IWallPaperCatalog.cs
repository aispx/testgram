namespace MyTelegram.Messenger.Services.WallPapers;

/// <summary>One row of the wallpaper catalogue — what exists, as opposed to what a user sees.</summary>
public sealed record WallPaperRow(
    long WallPaperId,
    long AccessHash,
    string Slug,
    long DocumentId,
    bool IsDefault,
    bool IsPattern,
    bool IsDark,
    bool ForChat,
    bool Listed,
    long CreatedBy,
    int Order,
    MyTelegram.Schema.IWallPaperSettings? Settings)
{
    /// <summary>A fill wallpaper carries no document, so it is a <c>wallPaperNoFile</c>.</summary>
    public bool IsFill => DocumentId == 0;
}

/// <summary>
/// The catalogue of wallpapers this server knows — the <c>wallpapers</c> collection — and the only place
/// that turns a row into a <c>WallPaper</c> constructor.
/// <para>See https://corefork.telegram.org/api/wallpapers</para>
/// </summary>
public interface IWallPaperCatalog
{
    Task<WallPaperRow?> FindByIdAsync(long wallPaperId);

    Task<WallPaperRow?> FindBySlugAsync(string slug);

    /// <summary>Every row named by either list, in no particular order.</summary>
    Task<List<WallPaperRow>> FindManyAsync(IReadOnlyCollection<long> wallPaperIds,
        IReadOnlyCollection<string> slugs);

    /// <summary>
    /// The wallpapers every account starts with, in catalogue order.
    ///
    /// <para>This is <b>not</b> the same thing as the <c>default</c> flag on the wire: real Telegram
    /// serves 83 wallpapers of which only 76 carry <c>default</c> (measured), so the flag describes the
    /// wallpaper and <c>Listed</c> describes whether it belongs to the starting list. Rows written before
    /// <c>Listed</c> existed fall back to <c>IsDefault</c>.</para>
    /// </summary>
    Task<List<WallPaperRow>> GetListedAsync();

    /// <summary>
    /// Builds the constructor. <paramref name="settings"/> overrides the ones the wallpaper was
    /// uploaded with, which is how a per-user or per-chat customization travels.
    /// <para>Returns null when the row names a document that does not exist: the wallpaper cannot be
    /// rendered and must be left out rather than served as unloadable media.</para>
    /// </summary>
    Task<MyTelegram.Schema.IWallPaper?> BuildAsync(WallPaperRow row, long selfUserId,
        MyTelegram.Schema.IWallPaperSettings? settings = null);

    /// <summary>
    /// A wallpaper that is nothing but its settings. Channels are given one this way: clients send
    /// <c>inputWallPaperNoFile{id = 0}</c> plus <c>settings.emoticon</c>, which names no catalogue row.
    /// </summary>
    MyTelegram.Schema.IWallPaper BuildFill(long wallPaperId, MyTelegram.Schema.IWallPaperSettings? settings,
        bool dark = false);

    /// <summary>Registers a wallpaper a user just uploaded and returns its row.</summary>
    Task<WallPaperRow> InsertUploadedAsync(long creatorUserId, long documentId, string mimeType, bool pattern,
        bool forChat, MyTelegram.Schema.IWallPaperSettings? settings);
}
