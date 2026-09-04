namespace MyTelegram;

public class PeerNotifySettings(
    bool? showPreviews,
    bool? silent,
    int? muteUntil,
    string? sound,
    NotificationSoundValue? iosSound = null,
    NotificationSoundValue? androidSound = null,
    NotificationSoundValue? otherSound = null,
    NotificationSoundValue? storiesIosSound = null,
    NotificationSoundValue? storiesAndroidSound = null,
    NotificationSoundValue? storiesOtherSound = null)
{
    public static PeerNotifySettings DefaultSettings { get; } = new(true, false, 0, "default");
    public int? MuteUntil { get; init; } = muteUntil; //= 0;// = int.MaxValue;

    public bool? ShowPreviews { get; init; } = showPreviews; //= true;
    public bool? Silent { get; init; } = silent; //= false;

    /// <summary>
    /// Legacy free-text sound name, kept because the encrypted push payload carries one of its own
    /// (<c>PushPayloadEncryptor</c>). The fields below are what
    /// <a href="https://corefork.telegram.org/api/ringtones#setting-notification-sounds">the API</a>
    /// actually exchanges.
    /// </summary>
    public string? Sound { get; init; } = sound; //= "default";

    /// <summary>
    /// The sound each platform should play. <c>inputPeerNotifySettings</c> carries a single <c>sound</c>
    /// field while <c>peerNotifySettings</c> reports three, because the server is what splits them per
    /// platform — "populating the ios_sound, android_sound or other_sound fields according to the platform
    /// where the sound should be played". A null field means the client's own default.
    ///
    /// <para>Which client reads which is not a guess: Telegram Android reads <c>android_sound</c>, and
    /// TelegramCore takes <c>ios_sound</c> under <c>#if os(iOS)</c> and <c>other_sound</c> otherwise — so
    /// Telegram for macOS reads the desktop field, not the iOS one.</para>
    /// </summary>
    public NotificationSoundValue? IosSound { get; init; } = iosSound;

    /// <inheritdoc cref="IosSound" />
    public NotificationSoundValue? AndroidSound { get; init; } = androidSound;

    /// <inheritdoc cref="IosSound" />
    public NotificationSoundValue? OtherSound { get; init; } = otherSound;

    /// <summary>
    /// The same, for stories (<c>stories_ios_sound</c> / <c>stories_android_sound</c> /
    /// <c>stories_other_sound</c>). The input side has one <c>stories_sound</c>, split the same way.
    /// </summary>
    public NotificationSoundValue? StoriesIosSound { get; init; } = storiesIosSound;

    /// <inheritdoc cref="StoriesIosSound" />
    public NotificationSoundValue? StoriesAndroidSound { get; init; } = storiesAndroidSound;

    /// <inheritdoc cref="StoriesIosSound" />
    public NotificationSoundValue? StoriesOtherSound { get; init; } = storiesOtherSound;
}
