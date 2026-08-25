namespace MyTelegram.Messenger.Services.Emoji;

/// <summary>
/// Which of the three curated custom-emoji lists a caller wants. They are separate lists on
/// Telegram — the profile-photo one is drawn from 39 packs, the group-photo one from 3 and the
/// accent-colour one from a single pack of monochrome icons — so they are stored and served
/// separately rather than derived from one another.
/// </summary>
public enum DefaultEmojiListKind
{
    /// <summary><c>account.getDefaultProfilePhotoEmojis</c>.</summary>
    ProfilePhoto,

    /// <summary><c>account.getDefaultGroupPhotoEmojis</c>.</summary>
    GroupPhoto,

    /// <summary><c>account.getDefaultBackgroundEmojis</c>.</summary>
    Background
}
