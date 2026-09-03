namespace MyTelegram;

/// <summary>Which kind of <c>NotificationSound</c> a stored value stands for.</summary>
public enum NotificationSoundKind
{
    /// <summary><c>notificationSoundDefault</c> — the client's own default sound.</summary>
    Default,

    /// <summary><c>notificationSoundNone</c> — silent.</summary>
    None,

    /// <summary><c>notificationSoundLocal</c> — a sound the OS provides, named by a client payload.</summary>
    Local,

    /// <summary><c>notificationSoundRingtone</c> — a sound uploaded through <c>account.uploadRingtone</c>.</summary>
    Ringtone
}

/// <summary>
/// A notification sound as stored on the server, i.e. what
/// <a href="https://corefork.telegram.org/api/ringtones#setting-notification-sounds">account.updateNotifySettings</a>
/// was given and what <c>peerNotifySettings</c> reports back.
///
/// <para>Deliberately a plain domain value rather than the TL <c>INotificationSound</c>: this lives inside
/// an aggregate event, and an event payload has to stay readable by whatever deserialises the event store
/// years later. The shape mirrors what <c>SetReactionsNotifySettingsHandler</c> already writes for the
/// reaction settings.</para>
/// </summary>
/// <param name="Kind">Which constructor this stands for.</param>
/// <param name="RingtoneId">Document id, for <see cref="NotificationSoundKind.Ringtone"/>.</param>
/// <param name="Title">Title, for <see cref="NotificationSoundKind.Local"/>.</param>
/// <param name="Data">Client-specific payload, for <see cref="NotificationSoundKind.Local"/>.</param>
public record NotificationSoundValue(
    NotificationSoundKind Kind,
    long RingtoneId = 0,
    string? Title = null,
    string? Data = null)
{
    public static NotificationSoundValue Default { get; } = new(NotificationSoundKind.Default);
}
