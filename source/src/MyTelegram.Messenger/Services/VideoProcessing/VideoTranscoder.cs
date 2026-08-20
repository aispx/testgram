using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.VideoProcessing;

/// <param name="Width">Pixel width of the video stream.</param>
/// <param name="Height">Pixel height of the video stream.</param>
/// <param name="DurationSeconds">Duration in seconds, rounded up.</param>
/// <param name="VideoCodec">Codec name as ffprobe reports it, e.g. <c>h264</c>.</param>
public sealed record VideoInfo(int Width, int Height, int DurationSeconds, string VideoCodec);

/// <summary>
/// Thin wrapper around ffprobe/ffmpeg used to build the alternative qualities of a video.
/// </summary>
public interface IVideoTranscoder
{
    Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encodes <paramref name="sourcePath"/> to the given height (width follows the aspect ratio).
    /// Returns false when ffmpeg fails or produces nothing usable.
    /// </summary>
    Task<bool> TranscodeAsync(string sourcePath, string destinationPath, int targetHeight,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class VideoTranscoder(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IFfmpegLocator ffmpegLocator,
    ILogger<VideoTranscoder> logger)
    : IVideoTranscoder, ITransientDependency
{
    private VideoProcessingConfig Config => options.CurrentValue.VideoProcessing;

    public async Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var ffprobe = ffmpegLocator.FfprobePath;
        if (ffprobe == null)
        {
            return null;
        }

        var arguments =
            $"-v error -select_streams v:0 -show_entries stream=width,height,codec_name -show_entries format=duration -of json \"{path}\"";
        var (exitCode, output, error) = await RunAsync(ffprobe, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffprobe failed for {Path}: {Error}", path, error);
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var stream = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            if (stream.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty;

            var duration = 0d;
            if (json.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationValue) &&
                double.TryParse(durationValue.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed))
            {
                duration = parsed;
            }

            return width > 0 && height > 0
                ? new VideoInfo(width, height, (int)Math.Ceiling(duration), codec)
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not read the ffprobe output for {Path}", path);
            return null;
        }
    }

    public async Task<bool> TranscodeAsync(string sourcePath, string destinationPath, int targetHeight,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ffmpegLocator.FfmpegPath;
        if (ffmpeg == null)
        {
            return false;
        }

        // -2 keeps the width even, which h264 requires; +faststart moves the index to the front so the
        // rendition can be streamed instead of downloaded whole.
        var arguments = $"-y -v error -i \"{sourcePath}\" -vf scale=-2:{targetHeight} " +
                        $"-c:v libx264 -preset {Config.Preset} -crf {Config.Crf} " +
                        $"-c:a aac -b:a {Config.AudioBitrate} -movflags +faststart \"{destinationPath}\"";

        var (exitCode, _, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffmpeg failed to produce the {Height}p rendition: {Error}", targetHeight, error);
            return false;
        }

        return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
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
        timeout.CancelAfter(TimeSpan.FromSeconds(Config.TimeoutSeconds));

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
