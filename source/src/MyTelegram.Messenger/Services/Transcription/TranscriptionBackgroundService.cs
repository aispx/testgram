using Microsoft.Extensions.Hosting;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>
/// Recognises the voice messages queued by <c>messages.transcribeAudio</c> and pushes the result as
/// <c>updateTranscribedAudio</c>.
///
/// <para><b>Why the work is queued rather than done in the handler.</b> tdlib caps the
/// <c>messages.transcribeAudio</c> request itself at eight seconds
/// (<c>TranscribeAudioQuery::send</c> sets <c>total_timeout_limit_ = 8</c>), which no download plus
/// transcode plus recognition round trip can fit inside. So the handler answers <c>pending</c> at once
/// and the text arrives as an update. The other end of the same constraint is that this must not be slow
/// either: tdlib gives a pending transcription 60 seconds
/// (<c>AUDIO_TRANSCRIPTION_TIMEOUT</c>) before failing it, which is why the poll delay here is a second
/// rather than the five <c>VideoProcessingBackgroundService</c> can afford.</para>
///
/// <para><b>A failure is pushed as an empty final update, not as silence.</b> The wire has no way to say
/// "recognition failed" — <c>updateTranscribedAudio</c> carries only <c>pending</c> and <c>text</c>. Left
/// silent, tdlib waits its full 60 seconds and Android waits forever: nothing in
/// <c>TranscribeButton</c> times an operation out, so the spinner stays until the app is restarted. An
/// empty final update is also exactly what Android writes for itself when the RPC fails
/// (<c>text = ""</c>, <c>isFinal = true</c>), so it is the one signal every client already handles.</para>
/// See https://corefork.telegram.org/api/transcribe
/// </summary>
public class TranscriptionBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<TranscriptionBackgroundService> logger)
    : BackgroundService
{
    /// <summary>
    /// Long enough for a download, a transcode and a recognition call, short enough that a worker killed
    /// mid-job releases the row while the client may still be listening.
    /// </summary>
    private const int LeaseSeconds = 120;

    /// <summary>Recognitions run at once. Each is mostly waiting on ffmpeg or the network.</summary>
    private const int BatchSize = 4;

    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TranscriptionBackgroundService started");

        await EnsureIndexesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessAsync(stoppingToken) == 0)
                {
                    await Task.Delay(PollDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while recognising queued voice messages");
                await Task.Delay(PollDelay, stoppingToken);
            }
        }

        logger.LogInformation("TranscriptionBackgroundService stopped");
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
            await store.EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The transcription indexes could not be prepared");
        }
    }

    private async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();

        var claimed = await store.ClaimAsync(BatchSize, LeaseSeconds, cancellationToken);
        if (claimed.Count == 0)
        {
            return 0;
        }

        await Task.WhenAll(claimed.Select(document => RecognizeAsync(document, cancellationToken)));

        return claimed.Count;
    }

    private async Task RecognizeAsync(TranscriptionDocument document, CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
        var preparer = scope.ServiceProvider.GetRequiredService<ITranscriptionAudioPreparer>();
        var client = scope.ServiceProvider.GetRequiredService<ISpeechRecognitionClient>();
        var notifier = scope.ServiceProvider.GetRequiredService<ITranscriptionUpdateNotifier>();
        var trialStore = scope.ServiceProvider.GetRequiredService<ITranscriptionTrialStore>();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<MyTelegramMessengerServerOptions>>();

        var maxAttempts = Math.Max(1, options.CurrentValue.Transcription.MaxAttempts);

        try
        {
            // Another row for the same document may have finished while this one waited - the same voice
            // note forwarded into two chats, or two accounts transcribing the same message.
            var cached = await store.GetCachedTextAsync(document.DocumentId, cancellationToken);
            if (cached != null)
            {
                await CompleteAsync(store, notifier, document, cached, cancellationToken);

                return;
            }

            if (!client.IsEnabled)
            {
                await FailAsync(store, notifier, trialStore, document,
                    "speech recognition is not configured", cancellationToken);

                return;
            }

            var prepared = await preparer.PrepareAsync(document.DocumentId, document.MimeType, cancellationToken);
            using var audio = prepared.Audio;

            if (audio == null)
            {
                await HandleFailureAsync(store, notifier, trialStore, document, prepared.Error,
                    prepared.Retryable, maxAttempts, cancellationToken);

                return;
            }

            var outcome = await client.RecognizeAsync(audio.Path, FileName(document, audio), audio.ContentType,
                cancellationToken);

            if (outcome.Result == null)
            {
                await HandleFailureAsync(store, notifier, trialStore, document, outcome.Error,
                    outcome.Retryable, maxAttempts, cancellationToken);

                return;
            }

            var text = outcome.Result.Text.Trim();

            // Cached even when empty: a voice note with no speech in it has been recognised, and asking
            // the provider again would cost the same and answer the same.
            await store.SaveCachedTextAsync(document.DocumentId, text, outcome.Result.Language, cancellationToken);
            await CompleteAsync(store, notifier, document, text, cancellationToken);

            logger.LogInformation(
                "Transcribed document {DocumentId} for user {UserId} ({Length} characters, transcription {TranscriptionId})",
                document.DocumentId, document.RequestedByUserId, text.Length, document.TranscriptionId);
        }
        catch (OperationCanceledException)
        {
            // Shutting down. The lease expires and another worker picks the row up.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while transcribing document {DocumentId}", document.DocumentId);

            await HandleFailureAsync(store, notifier, trialStore, document, ex.Message, true, maxAttempts,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Only the OpenAI-compatible multipart shape carries a filename, and some gateways sniff the format
    /// from its extension rather than from the declared content type — so it has to match the body.
    /// </summary>
    private static string FileName(TranscriptionDocument document, PreparedAudio audio)
    {
        var extension = audio.ContentType.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/webm" or "video/webm" => ".webm",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" or "audio/aac" => ".m4a",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/flac" or "audio/x-flac" => ".flac",
            _ => ".mp3"
        };

        return $"voice-{document.DocumentId}{extension}";
    }

    private static async Task CompleteAsync(ITranscriptionStore store, ITranscriptionUpdateNotifier notifier,        TranscriptionDocument document, string text, CancellationToken cancellationToken)
    {
        await store.CompleteAsync(document.Id, text, cancellationToken);
        await notifier.NotifyAsync(document, text, false);
    }

    private async Task HandleFailureAsync(ITranscriptionStore store, ITranscriptionUpdateNotifier notifier,
        ITranscriptionTrialStore trialStore, TranscriptionDocument document, string? error, bool retryable,
        int maxAttempts, CancellationToken cancellationToken)
    {
        if (retryable && document.Attempts < maxAttempts)
        {
            // Backoff stays inside tdlib's 60 second window; the point is to survive one bad response,
            // not to keep trying long after the client has given up.
            var nextAttemptDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5 * document.Attempts;

            logger.LogWarning(
                "Could not transcribe document {DocumentId} (attempt {Attempt}/{MaxAttempts}): {Error}. Retrying at {Date}",
                document.DocumentId, document.Attempts, maxAttempts, error, nextAttemptDate);

            await store.ReleaseAsync(document.Id, document.Attempts, nextAttemptDate, cancellationToken);

            return;
        }

        await FailAsync(store, notifier, trialStore, document, error, cancellationToken);
    }

    private async Task FailAsync(ITranscriptionStore store, ITranscriptionUpdateNotifier notifier,
        ITranscriptionTrialStore trialStore, TranscriptionDocument document, string? error,
        CancellationToken cancellationToken)
    {
        logger.LogError(
            "Giving up on transcribing document {DocumentId} for user {UserId} after {Attempts} attempts: {Error}",
            document.DocumentId, document.RequestedByUserId, document.Attempts, error);

        await store.FailAsync(document.Id, cancellationToken);

        if (document.TrialConsumed)
        {
            // Nobody should lose one of three weekly tries to a provider outage.
            await trialStore.RefundAsync(document.RequestedByUserId, cancellationToken);

            logger.LogInformation("Refunded the transcription trial try of user {UserId}",
                document.RequestedByUserId);
        }

        await notifier.NotifyAsync(document, string.Empty, false);
    }
}
