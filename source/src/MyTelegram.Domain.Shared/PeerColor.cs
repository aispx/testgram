namespace MyTelegram;

/// <summary>
/// A peer's <a href="https://core.telegram.org/api/colors">accent color / profile color</a>.
/// <para>
/// Either a palette color (<see cref="Color"/> plus an optional <see cref="BackgroundEmojiId"/>), or a
/// collectible color taken from a unique star gift, in which case <see cref="CollectibleId"/> is set
/// and the remaining fields describe the gift's own palette.
/// </para>
/// </summary>
public record PeerColor(
    int? Color,
    long? BackgroundEmojiId,
    long? CollectibleId = null,
    long? GiftEmojiId = null,
    int? AccentColor = null,
    IReadOnlyList<int>? Colors = null,
    int? DarkAccentColor = null,
    IReadOnlyList<int>? DarkColors = null);
