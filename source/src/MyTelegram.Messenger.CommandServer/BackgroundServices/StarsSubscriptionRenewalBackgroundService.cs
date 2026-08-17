using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Drives <see cref="IStarsSubscriptionRenewalService"/>: renews channel Star subscriptions whose
/// period has run out and warns users whose balance will not cover the next renewal.
/// </summary>
public class StarsSubscriptionRenewalBackgroundService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<StarsSubscriptionRenewalBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Star subscription renewal worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                var renewalService = scope.ServiceProvider.GetRequiredService<IStarsSubscriptionRenewalService>();
                await renewalService.ProcessDueSubscriptionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Star subscription renewal worker error");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
