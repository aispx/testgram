using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Services.VideoProcessing;

/// <summary>
/// Converts the videos of the messages parked in the schedule queue and releases them once the
/// alternative qualities are ready.
/// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
/// </summary>
public class VideoProcessingBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<VideoProcessingBackgroundService> logger)
    : BackgroundService
{
    /// <summary>Conversion can be slow, so the claim is held for a long time.</summary>
    private const int LeaseSeconds = 3600;

    private const int MaxAttempts = 3;

    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("VideoProcessingBackgroundService started");

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
                logger.LogError(ex, "Error while converting the videos of the schedule queue");
                await Task.Delay(PollDelay, stoppingToken);
            }
        }

        logger.LogInformation("VideoProcessingBackgroundService stopped");
    }

    private async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IScheduledMessageDispatcher>();
        var videoProcessingService = scope.ServiceProvider.GetRequiredService<IVideoProcessingService>();

        // One at a time: a conversion is CPU bound and there is no point in running several of them
        // against each other on the same server.
        var documents = await store.ClaimVideoProcessingAsync(1, LeaseSeconds, cancellationToken);
        if (documents.Count == 0)
        {
            return 0;
        }

        foreach (var document in documents)
        {
            try
            {
                if (document.Item.Media is TMessageMediaDocument { Document: TDocument source } media)
                {
                    var altDocuments = await videoProcessingService.CreateAltDocumentsAsync(source, cancellationToken);
                    if (altDocuments.Count > 0)
                    {
                        media.AltDocuments = new TVector<IDocument>(altDocuments);
                    }
                }

                document.VideoProcessingPending = false;
                await dispatcher.FlushAsync([document]);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(store, dispatcher, document, ex);
            }
        }

        return documents.Count;
    }

    /// <summary>
    /// A conversion that keeps failing must not swallow the message: after a few attempts the video is
    /// delivered exactly as it was uploaded, without alternative qualities.
    /// </summary>
    private async Task HandleFailureAsync(IScheduledMessageStore store, IScheduledMessageDispatcher dispatcher,
        ScheduledMessageDocument document, Exception exception)
    {
        if (document.Attempts + 1 >= MaxAttempts)
        {
            logger.LogError(exception,
                "Video of scheduled message {Id} could not be converted after {Attempts} attempts, sending it unprocessed",
                document.Id, document.Attempts + 1);

            document.VideoProcessingPending = false;
            await dispatcher.FlushAsync([document]);
            return;
        }

        var nextAttemptDate = DateTime.UtcNow.ToTimestamp() + 60 * (document.Attempts + 1);
        logger.LogWarning(exception, "Could not convert the video of scheduled message {Id}, retrying at {Date}",
            document.Id, nextAttemptDate);

        await store.ReleaseAsync(document, nextAttemptDate);
    }
}
