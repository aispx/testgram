using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Drives <see cref="CallSessionExpiryService"/> on a timer so abandoned 1:1 call sessions are torn down
/// instead of leaving both participants permanently marked as busy.
/// </summary>
public class CallSessionExpiryBackgroundService(
    CallSessionExpiryService expiryService,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<CallSessionExpiryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Call session expiry worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await expiryService.SweepAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} stale call session(s)", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Call session expiry worker error");
            }

            var intervalSeconds = Math.Max(1, options.CurrentValue.Calls?.ExpirySweepIntervalSeconds ?? 10);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
