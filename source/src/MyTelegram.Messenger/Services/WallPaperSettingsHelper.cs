namespace MyTelegram.Messenger.Services;

/// <summary>
/// Keeps a <c>wallPaperSettings</c> serializable.
///
/// <para><b>Telegram's own schema gives <c>second_background_color</c> and <c>rotation</c> the same flag
/// bit</b> — both are <c>flags.4</c> in
/// <a href="https://corefork.telegram.org/constructor/wallPaperSettings">wallPaperSettings</a>, because a
/// gradient always carries the two together. The generated serializer transcribes that faithfully: setting
/// either one raises bit 4, and it then writes <c>SecondBackgroundColor.Value</c> <i>and</i>
/// <c>Rotation.Value</c>.</para>
///
/// <para>So a stored wallpaper with a second colour and no rotation throws
/// <c>InvalidOperationException: Nullable object must have a value</c> inside
/// <c>TWallPaperSettings.Serialize</c> — and because that happens while the response is being written, the
/// caller is never answered at all. One such row (the seeded <c>gradient-rainbow</c>) took the whole of
/// <c>account.getWallPapers</c> down with it, with nothing in the log but the serializer's stack trace and
/// a handler that had already reported success.</para>
/// </summary>
internal static class WallPaperSettingsHelper
{
    /// <summary>
    /// Fills in whichever half of the shared <c>flags.4</c> pair is missing, so the pair is either wholly
    /// present or wholly absent.
    /// </summary>
    public static MyTelegram.Schema.TWallPaperSettings? PairSharedFlags(
        MyTelegram.Schema.TWallPaperSettings? settings)
    {
        if (settings == null)
        {
            return null;
        }

        if (settings.SecondBackgroundColor.HasValue || settings.Rotation.HasValue)
        {
            settings.SecondBackgroundColor ??= 0;
            settings.Rotation ??= 0;
        }

        return settings;
    }
}
