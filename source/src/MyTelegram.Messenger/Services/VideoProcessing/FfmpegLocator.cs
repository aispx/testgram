using System.Runtime.InteropServices;

namespace MyTelegram.Messenger.Services.VideoProcessing;

/// <summary>
/// Resolves the ffmpeg/ffprobe executables in a way that works on Linux, Windows and macOS, so the
/// same build runs everywhere: video processing simply switches itself off when the binaries are not
/// installed instead of failing every send.
/// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
/// </summary>
public interface IFfmpegLocator
{
    /// <summary>True when both ffmpeg and ffprobe were found.</summary>
    bool IsAvailable { get; }

    /// <summary>Absolute path (or bare command) of ffmpeg, or null when it could not be found.</summary>
    string? FfmpegPath { get; }

    /// <summary>Absolute path (or bare command) of ffprobe, or null when it could not be found.</summary>
    string? FfprobePath { get; }
}

/// <inheritdoc />
public class FfmpegLocator : IFfmpegLocator, ISingletonDependency
{
    private readonly Lazy<(string? Ffmpeg, string? Ffprobe)> _resolved;

    public FfmpegLocator(IOptions<MyTelegramMessengerServerOptions> options,
        ILogger<FfmpegLocator> logger)
    {
        _resolved = new Lazy<(string?, string?)>(() =>
        {
            var config = options.Value.VideoProcessing;
            var ffmpeg = Resolve("ffmpeg", config.FfmpegPath);
            var ffprobe = Resolve("ffprobe", config.FfprobePath);

            if (ffmpeg == null || ffprobe == null)
            {
                logger.LogWarning(
                    "ffmpeg/ffprobe were not found (ffmpeg={Ffmpeg}, ffprobe={Ffprobe}); server-side video " +
                    "processing is disabled. Install ffmpeg or set App__VideoProcessing__FfmpegPath.",
                    ffmpeg ?? "missing", ffprobe ?? "missing");
            }
            else
            {
                logger.LogInformation("Video processing uses ffmpeg={Ffmpeg}, ffprobe={Ffprobe}", ffmpeg, ffprobe);
            }

            return (ffmpeg, ffprobe);
        });
    }

    public bool IsAvailable => _resolved.Value is { Ffmpeg: not null, Ffprobe: not null };
    public string? FfmpegPath => _resolved.Value.Ffmpeg;
    public string? FfprobePath => _resolved.Value.Ffprobe;

    private static string? Resolve(string executable, string? configuredPath)
    {
        // 1. An explicit configured path wins, whether it is a full path or a bare command name.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (Path.IsPathRooted(configuredPath))
            {
                return File.Exists(configuredPath) ? configuredPath : null;
            }

            // A bare name that differs from the default is honoured as-is (it must be on PATH).
            if (!string.Equals(configuredPath, executable, StringComparison.OrdinalIgnoreCase))
            {
                return configuredPath;
            }
        }

        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{executable}.exe" : executable;

        // 2. Walk PATH — the normal case in the container and on a developer box that installed ffmpeg.
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 3. Common install locations that are frequently not on the service's PATH.
        foreach (var candidate in CommonInstallPaths(fileName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CommonInstallPaths(string fileName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetEnvironmentVariable("ChocolateyInstall"),
                         @"C:\ffmpeg"
                     })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    yield return Path.Combine(root, "ffmpeg", "bin", fileName);
                    yield return Path.Combine(root, "bin", fileName);
                    yield return Path.Combine(root, fileName);
                }
            }

            yield break;
        }

        // Linux and macOS (including Homebrew on both Intel and Apple Silicon).
        yield return $"/usr/bin/{fileName}";
        yield return $"/usr/local/bin/{fileName}";
        yield return $"/opt/homebrew/bin/{fileName}";
        yield return $"/snap/bin/{fileName}";
    }
}
