using System.Diagnostics;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>Audio ready to be handed to recognition. Delete <see cref="Path"/> when done.</summary>
/// <param name="Path">Absolute path of the file to send.</param>
/// <param name="SizeBytes">Its size, measured against <c>App__Transcription__MaxUploadBytes</c>.</param>
/// <param name="ContentType">
/// What to declare it as. The stored mime type when the body is sent unchanged, <c>audio/mpeg</c> when it
/// was transcoded.
/// </param>
public sealed record PreparedAudio(string Path, long SizeBytes, string ContentType) : IDisposable
{
    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // A temp file that cannot be removed is not worth failing a transcription over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <param name="Audio">Non-null on success.</param>
/// <param name="Retryable">Whether another attempt could succeed (a transient object-store read).</param>
/// <param name="Error">What to log.</param>
public sealed record PreparedAudioOutcome(PreparedAudio? Audio, bool Retryable, string? Error)
{
    public static PreparedAudioOutcome Success(PreparedAudio audio) => new(audio, false, null);

    public static PreparedAudioOutcome Fail(string error, bool retryable) => new(null, retryable, error);
}

/// <summary>
/// Fetches the stored body of a voice note or round video and, only when the recognition provider will not
/// take it as it is, converts it.
///
/// <para><b>Deepgram needs no conversion</b>, which is measured rather than assumed: it transcribes a
/// Telegram voice note (OGG OPUS) and a round video note (MP4, audio pulled out of the container) directly.
/// So on the default provider this is a download and nothing more, and ffmpeg is not required for
/// transcription at all.</para>
///
/// <para><b>The OpenAI-compatible shape does</b>: it refuses OGG outright (measured against VoidAI, whose
/// docs claim otherwise), so the body becomes MP3 first. <c>-vn -ac 1 -ar 16000</c> also drops the video
/// track of a round message, of which only the audio was ever of interest, and cuts a minute of speech to
/// roughly 120 KB.</para>
/// </summary>
public interface ITranscriptionAudioPreparer
{
    /// <summary>
    /// The body of <paramref name="documentId"/>, converted if the provider requires it.
    /// <paramref name="mimeType"/> is the stored document's own type, which decides whether it does.
    /// </summary>
    Task<PreparedAudioOutcome> PrepareAsync(long documentId, string? mimeType,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TranscriptionAudioPreparer(
    IStoredFileStorage storedFileStorage,
    ISpeechRecognitionClient speechRecognitionClient,
    IFfmpegLocator ffmpegLocator,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TranscriptionAudioPreparer> logger)
    : ITranscriptionAudioPreparer, ITransientDependency
{
    public async Task<PreparedAudioOutcome> PrepareAsync(long documentId, string? mimeType,
        CancellationToken cancellationToken = default)
    {
        var config = options.CurrentValue.Transcription;
        var asIs = speechRecognitionClient.AcceptsAsIs(mimeType);
        var ffmpeg = asIs ? null : ffmpegLocator.FfmpegPath;

        if (!asIs && ffmpeg == null)
        {
            return PreparedAudioOutcome.Fail(
                $"the recognition provider does not accept {mimeType ?? "an unknown type"} and ffmpeg is not " +
                "installed, so it cannot be converted into a format that it does", false);
        }

        var sourcePath = Path.Combine(Path.GetTempPath(), $"transcribe-{documentId}-{Guid.NewGuid():N}");
        var destinationPath = sourcePath + ".mp3";
        var keepSource = false;

        try
        {
            if (!await storedFileStorage.DownloadToFileAsync(documentId, sourcePath, cancellationToken))
            {
                return PreparedAudioOutcome.Fail(
                    $"the body of document {documentId} could not be read out of the object store", true);
            }

            if (asIs)
            {
                var source = new FileInfo(sourcePath);
                var outcome = Measure(new PreparedAudio(sourcePath, source.Length, mimeType!), documentId,
                    config.MaxUploadBytes);

                keepSource = outcome.Audio != null;

                return outcome;
            }

            // -vn drops the video track of a round message; mono 16 kHz is what speech recognition
            // downsamples to anyway, and it keeps a long voice note far inside the upload cap.
            var arguments =
                $"-y -i \"{sourcePath}\" -vn -ac 1 -ar 16000 -c:a libmp3lame -q:a 5 \"{destinationPath}\"";

            var (exitCode, error) = await RunAsync(ffmpeg!, arguments, config.TimeoutSeconds, cancellationToken);
            if (exitCode != 0)
            {
                return PreparedAudioOutcome.Fail($"ffmpeg could not convert document {documentId}: {error}", false);
            }

            var converted = new FileInfo(destinationPath);
            if (!converted.Exists || converted.Length == 0)
            {
                return PreparedAudioOutcome.Fail($"ffmpeg produced no audio for document {documentId}", false);
            }

            return Measure(new PreparedAudio(destinationPath, converted.Length, "audio/mpeg"), documentId,
                config.MaxUploadBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The audio of document {DocumentId} could not be prepared", documentId);

            return PreparedAudioOutcome.Fail($"the audio could not be prepared: {ex.Message}", true);
        }
        finally
        {
            if (!keepSource)
            {
                Delete(sourcePath);
            }
        }
    }

    private static PreparedAudioOutcome Measure(PreparedAudio audio, long documentId, long maxUploadBytes)
    {
        if (maxUploadBytes > 0 && audio.SizeBytes > maxUploadBytes)
        {
            audio.Dispose();

            return PreparedAudioOutcome.Fail(
                $"the audio of document {documentId} is {audio.SizeBytes} bytes, over the {maxUploadBytes} " +
                "byte limit of the recognition provider", false);
        }

        return PreparedAudioOutcome.Success(audio);
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string executable, string arguments,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return (-1, $"{executable} could not be started");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));

        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);

            return (-1, $"{executable} did not finish within {timeoutSeconds}s");
        }

        var error = await errorTask;

        return (process.ExitCode, error);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
