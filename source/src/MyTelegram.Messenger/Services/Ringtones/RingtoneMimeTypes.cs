namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// What may be a notification sound.
///
/// <para>"Supported formats: MP3, OGG OPUS" — <c>account.uploadRingtone</c> refuses anything else with
/// <c>RINGTONE_MIME_INVALID</c>. Telegram Android will also display an already existing <c>audio/m4a</c>
/// document as a tone (<c>RingtoneDataStore.ringtoneSupportedMimeType</c>), so a document already stored
/// on the server may be saved under that type too — but a fresh upload may not, because the page names
/// only the two formats.</para>
/// See https://corefork.telegram.org/api/ringtones
/// </summary>
public static class RingtoneMimeTypes
{
    public const string Mp3 = "audio/mpeg";

    /// <summary>
    /// The MIME types <c>account.uploadRingtone</c> accepts. <c>audio/mp3</c> is not a registered type
    /// but is what some clients send for the same bytes, and <c>audio/opus</c> likewise for OGG OPUS.
    /// </summary>
    public static readonly string[] Uploadable = ["audio/mpeg", "audio/mp3", "audio/ogg", "audio/opus"];

    /// <summary>
    /// The MIME types <c>account.saveRingtone</c> accepts for an already stored document — the
    /// uploadable ones plus what Android is willing to play from its tone list.
    /// </summary>
    public static readonly string[] Saveable =
        ["audio/mpeg", "audio/mp3", "audio/mpeg3", "audio/ogg", "audio/opus", "audio/m4a", "audio/mp4"];

    public static bool IsUploadable(string? mimeType) => Contains(Uploadable, mimeType);

    public static bool IsSaveable(string? mimeType) => Contains(Saveable, mimeType);

    /// <summary>
    /// Whether the sound is already MP3, which is what decides between <c>account.savedRingtone</c> and
    /// <c>account.savedRingtoneConverted</c>: "If the notification sound is already in MP3 format,
    /// account.savedRingtone will be returned. Otherwise, it will be automatically converted".
    /// </summary>
    public static bool IsMp3(string? mimeType) =>
        string.Equals(mimeType, "audio/mpeg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, "audio/mp3", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mimeType, "audio/mpeg3", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string[] allowed, string? mimeType)
    {
        return !string.IsNullOrWhiteSpace(mimeType) &&
               allowed.Contains(mimeType.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
