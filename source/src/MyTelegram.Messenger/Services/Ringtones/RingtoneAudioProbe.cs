using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>What ffprobe could read out of an audio file.</summary>
/// <param name="DurationSeconds">
/// Rounded to the nearest second, which is the number a client compares against
/// <c>ringtone_duration_max</c>. Rounding <i>up</i> would refuse a genuinely five-second tone as six: an MP3
/// encoder pads the stream, so a 3.0 s source comes back as 3.02 s.
/// </param>
public sealed record RingtoneAudioInfo(int DurationSeconds, string? Title, string? Performer);

/// <summary>
/// Reads the duration and tags of an audio file, and converts one to MP3.
///
/// <para><see cref="IVideoTranscoder.ProbeAsync"/> cannot be used for either: it selects the first
/// <b>video</b> stream and returns null unless it has a width and a height, so on an MP3 or an OGG OPUS
/// it always fails. The duration is not cosmetic — it is the only way to enforce
/// <c>ringtone_duration_max</c>, and <c>documentAttributeAudio</c> requires it, so without a probe the
/// server would have to either invent a number or ship a sound with no audio attribute at all.</para>
/// </summary>
public interface IRingtoneAudioProbe
{
    /// <summary>True when ffmpeg and ffprobe are both installed.</summary>
    bool IsAvailable { get; }

    /// <summary>Probes <paramref name="path"/>, or returns null when ffprobe is missing or fails.</summary>
    Task<RingtoneAudioInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encodes <paramref name="sourcePath"/> into an MP3 at <paramref name="destinationPath"/>,
    /// dropping any video stream (cover art) so the result is a plain audio file. Returns false when
    /// ffmpeg is missing, fails, or produces nothing usable — the caller then keeps the original rather
    /// than replacing it with something broken.
    /// </summary>
    Task<bool> ConvertToMp3Async(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class RingtoneAudioProbe(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IFfmpegLocator ffmpegLocator,
    ILogger<RingtoneAudioProbe> logger)
    : IRingtoneAudioProbe, ITransientDependency
{
    public bool IsAvailable => ffmpegLocator.IsAvailable;

    private int TimeoutSeconds => Math.Max(5, options.CurrentValue.VideoProcessing.TimeoutSeconds);

    public async Task<RingtoneAudioInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var ffprobe = ffmpegLocator.FfprobePath;
        if (ffprobe == null)
        {
            return null;
        }

        var arguments =
            "-v error -select_streams a:0 -show_entries stream=codec_name:format=duration:format_tags=title,artist,album_artist " +
            $"-of json \"{path}\"";

        var (exitCode, output, error) = await RunAsync(ffprobe, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffprobe failed for the notification sound {Path}: {Error}", path, error);

            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output);

            // No audio stream at all means the file is not a sound, whatever its declared mime type says.
            if (!json.RootElement.TryGetProperty("streams", out var streams) ||
                streams.EnumerateArray().FirstOrDefault().ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!json.RootElement.TryGetProperty("format", out var format))
            {
                return null;
            }

            var duration = 0d;
            if (format.TryGetProperty("duration", out var durationValue) &&
                double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed))
            {
                duration = parsed;
            }

            var title = ReadTag(format, "title");
            var performer = ReadTag(format, "artist") ?? ReadTag(format, "album_artist");

            return new RingtoneAudioInfo((int)Math.Round(duration, MidpointRounding.AwayFromZero), title,
                performer);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "ffprobe returned output that could not be parsed for {Path}", path);

            return null;
        }
    }

    public async Task<bool> ConvertToMp3Async(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ffmpegLocator.FfmpegPath;
        if (ffmpeg == null)
        {
            return false;
        }

        // -vn drops embedded cover art, which would otherwise make the result a video file as far as
        // ffprobe and the clients are concerned.
        var arguments =
            $"-y -i \"{sourcePath}\" -vn -map_metadata 0 -c:a libmp3lame -q:a 4 \"{destinationPath}\"";

        var (exitCode, _, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffmpeg could not convert {Path} to MP3: {Error}", sourcePath, error);

            return false;
        }

        var file = new FileInfo(destinationPath);
        if (!file.Exists || file.Length == 0)
        {
            logger.LogWarning("ffmpeg produced no MP3 for {Path}", sourcePath);

            return false;
        }

        return true;
    }

    private static string? ReadTag(JsonElement format, string name)
    {
        if (!format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase) &&
                tag.Value.ValueKind == JsonValueKind.String)
            {
                var value = tag.Value.GetString();

                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string fileName, string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }

        return (process.ExitCode, await outputTask, await errorTask);
    }
}
