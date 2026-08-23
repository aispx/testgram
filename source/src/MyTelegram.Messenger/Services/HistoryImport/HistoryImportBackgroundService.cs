using Microsoft.Extensions.Hosting;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Executes the imports queued by <c>messages.startHistoryImport</c>.
/// See https://corefork.telegram.org/api/import
/// </summary>
public class HistoryImportBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<HistoryImportBackgroundService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HistoryImport.Enabled)
        {
            logger.LogInformation("HistoryImportBackgroundService is disabled by configuration");
            return;
        }

        logger.LogInformation("HistoryImportBackgroundService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IHistoryImportStore>();
                await store.EnsureIndexesAsync(stoppingToken);
                break;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not create the history import indexes, retrying");
                await Task.Delay(PollDelay, stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IHistoryImportRunner>();

                // One import at a time: the messages of a single chat have to keep their order, and
                // the send pipeline is shared with every other client of this server.
                if (!await runner.RunNextAsync(stoppingToken))
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
                logger.LogError(ex, "Error while importing a chat history");
                await Task.Delay(PollDelay, stoppingToken);
            }
        }

        logger.LogInformation("HistoryImportBackgroundService stopped");
    }
}
