namespace MyTelegram.Converters;

/// <summary>
/// TL <c>NotificationSound</c> ↔ the stored <see cref="NotificationSoundValue"/>.
///
/// <para>Lives here because both directions are needed on both sides of the wire: the handler that stores
/// what <c>account.updateNotifySettings</c> was given, and the mappers that report it back in
/// <c>peerNotifySettings</c> (<c>account.getNotifySettings</c>, <c>users.getFullUser</c>,
/// <c>messages.getDialogs</c>).</para>
/// See https://corefork.telegram.org/api/ringtones#setting-notification-sounds
/// </summary>
public static class NotificationSoundConverter
{
    /// <summary>
    /// The stored form of what a client sent, or null when the field was absent — which means "leave the
    /// current sound alone", not "play the default".
    /// </summary>
    public static NotificationSoundValue? ToValue(INotificationSound? sound)
    {
        return sound switch
        {
            null => null,
            TNotificationSoundDefault => NotificationSoundValue.Default,
            TNotificationSoundNone => new NotificationSoundValue(NotificationSoundKind.None),
            TNotificationSoundLocal local => new NotificationSoundValue(NotificationSoundKind.Local,
                Title: local.Title, Data: local.Data),
            TNotificationSoundRingtone ringtone => new NotificationSoundValue(NotificationSoundKind.Ringtone,
                ringtone.Id),
            // An unknown constructor is treated as absent rather than as the default, so a newer client
            // cannot silently reset a sound the user chose.
            _ => null
        };
    }

    /// <summary>
    /// What to report in <c>peerNotifySettings</c>. Null stays null: the field is optional and an absent one
    /// is how the server says "the client's own default", which is what every client falls back to.
    /// </summary>
    public static INotificationSound? ToTl(NotificationSoundValue? value)
    {
        return value?.Kind switch
        {
            null => null,
            NotificationSoundKind.Default => new TNotificationSoundDefault(),
            NotificationSoundKind.None => new TNotificationSoundNone(),
            NotificationSoundKind.Local => new TNotificationSoundLocal
            {
                Title = value.Title ?? string.Empty,
                Data = value.Data ?? string.Empty
            },
            NotificationSoundKind.Ringtone => new TNotificationSoundRingtone { Id = value.RingtoneId },
            _ => null
        };
    }

    /// <summary>
    /// Which of the three platform fields a sound belongs in. <c>inputPeerNotifySettings</c> carries one
    /// <c>sound</c> and <c>peerNotifySettings</c> reports one per platform, so the split is the server's:
    /// "populating the ios_sound, android_sound or other_sound fields according to the platform where the
    /// sound should be played."
    ///
    /// <para>Which client reads which is measured from their sources, not assumed: Telegram Android reads
    /// <c>android_sound</c>, and TelegramCore takes <c>ios_sound</c> under <c>#if os(iOS)</c> and
    /// <c>other_sound</c> otherwise — so Telegram for macOS belongs with the desktop clients. A session
    /// whose platform is unknown fills all three, because a <c>notificationSoundRingtone</c> id means the
    /// same thing everywhere and dropping the user's choice is worse than storing it too widely.</para>
    /// </summary>
    public static (NotificationSoundValue? Ios, NotificationSoundValue? Android, NotificationSoundValue? Other)
        SplitByPlatform(NotificationSoundValue? sound, DeviceType deviceType)
    {
        if (sound == null)
        {
            return (null, null, null);
        }

        return deviceType switch
        {
            DeviceType.Ios => (sound, null, null),
            DeviceType.Android or DeviceType.AndroidX => (null, sound, null),
            DeviceType.Desktop or DeviceType.MacOs or DeviceType.WebA or DeviceType.WebK or DeviceType.TdLib
                or DeviceType.Unigram => (null, null, sound),
            _ => (sound, sound, sound)
        };
    }
}
