using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AdminLog;

/// <summary>
/// Creates the <a href="https://corefork.telegram.org/api/recent-actions">admin log</a> indexes at startup,
/// including the TTL index that enforces the retention window.
/// </summary>
public sealed class AdminLogIndexInitializer(
    IMongoDatabase database,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<AdminLogIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AdminLogCollection.EnsureIndexesAsync(
                database,
                options.Value.AdminLogRetentionSeconds,
                logger,
                cancellationToken);
        }
        catch (Exception e)
        {
            // A missing index degrades admin log queries, it must not stop the server from starting.
            logger.LogError(e, "Failed to create the admin log indexes");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
