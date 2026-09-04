namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// The three numbers that bound a notification sound, from
/// <a href="https://corefork.telegram.org/api/config">appConfig</a>.
///
/// <para>They have to be read from the same place the client was told, because the client displays the
/// limit it was given in the error message it builds from ours: Telegram Android formats
/// <c>ErrorRingtoneSizeTooBig</c> with <c>ringtoneSizeMax / 1024</c> and
/// <c>ErrorRingtoneDurationTooLong</c> with <c>ringtoneDurationMax</c>. Refusing at a different number
/// than the one advertised would produce a message that contradicts itself.</para>
/// </summary>
public interface IRingtoneLimits
{
    /// <summary><c>ringtone_size_max</c> — bytes.</summary>
    int MaxSizeBytes { get; }

    /// <summary><c>ringtone_duration_max</c> — seconds.</summary>
    int MaxDurationSeconds { get; }

    /// <summary><c>ringtone_saved_count_max</c> — how many sounds one account may keep.</summary>
    int MaxSavedCount { get; }
}

/// <inheritdoc />
public class RingtoneLimits(IAppConfigHelper appConfigHelper) : IRingtoneLimits, ITransientDependency
{
    /// <summary>Fallbacks match what <c>AppConfigHelper</c> emits, and tdesktop's own defaults.</summary>
    public const int SizeFallback = 307200;

    public const int DurationFallback = 5;
    public const int SavedCountFallback = 100;

    public int MaxSizeBytes => appConfigHelper.GetInt32Value("ringtone_size_max", SizeFallback);

    public int MaxDurationSeconds => appConfigHelper.GetInt32Value("ringtone_duration_max", DurationFallback);

    public int MaxSavedCount => appConfigHelper.GetInt32Value("ringtone_saved_count_max", SavedCountFallback);
}
