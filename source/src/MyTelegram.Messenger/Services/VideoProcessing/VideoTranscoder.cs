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

    /// <summary>
    /// Converts an animated image (a real <c>.gif</c>) into the silent MPEG4 that Telegram calls a
    /// GIF: "On Telegram, GIFs are actually MPEG4 videos without sound; if the user tries to upload
    /// an actual GIF file, it will be automatically converted to an MPEG4 file by the server."
    /// Returns false when ffmpeg is unavailable, fails, or produces nothing usable.
    /// See https://corefork.telegram.org/api/gifs#uploading-gifs
    /// </summary>
    Task<bool> ConvertGifToMp4Async(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the first frame of <paramref name="sourcePath"/> to <paramref name="destinationPath"/> as a
    /// JPEG no larger than <paramref name="maxSize"/> on its longer side.
    ///
    /// <para>A document without <c>document.thumbs</c> has nothing for a client to draw until the whole
    /// file has arrived, so a GIF the server produced looks like it is loading forever even when it is
    /// not.</para>
    /// </summary>
    Task<bool> ExtractThumbnailAsync(string sourcePath, string destinationPath, int maxSize,
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

    public async Task<bool> ConvertGifToMp4Async(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ffmpegLocator.FfmpegPath;
        if (ffmpeg == null)
        {
            return false;
        }

        // -an drops audio, which is what makes it a GIF rather than a video as far as clients are
        // concerned. yuv420p and the even-dimension crop are what h264 accepts and what every player
        // can decode; a GIF is frequently an odd number of pixels wide, and without the crop ffmpeg
        // simply refuses. -loop 0 on the input decodes the animation once rather than forever.
        var arguments = $"-y -v error -i \"{sourcePath}\" -an " +
                        "-vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                        $"-c:v libx264 -preset {Config.Preset} -crf {Config.Crf} " +
                        $"-pix_fmt yuv420p -movflags +faststart \"{destinationPath}\"";

        var (exitCode, _, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffmpeg failed to convert an animation to MPEG4: {Error}", error);
            return false;
        }

        return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
    }

    public async Task<bool> ExtractThumbnailAsync(string sourcePath, string destinationPath, int maxSize,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ffmpegLocator.FfmpegPath;
        if (ffmpeg == null)
        {
            return false;
        }

        // Scale the longer side down to maxSize and leave anything smaller alone: a thumbnail larger than
        // the frame is wasted bytes, and force_original_aspect_ratio keeps the tile from being stretched.
        var arguments = $"-y -v error -i \"{sourcePath}\" -frames:v 1 " +
                        $"-vf \"scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease\" " +
                        $"-f mjpeg \"{destinationPath}\"";

        var (exitCode, _, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
        if (exitCode != 0)
        {
            logger.LogWarning("ffmpeg failed to extract a thumbnail: {Error}", error);

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
