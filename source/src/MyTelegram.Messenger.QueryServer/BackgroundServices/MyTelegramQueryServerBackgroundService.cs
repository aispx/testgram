using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

public class MyTelegramQueryServerBackgroundService(
    ILogger<MyTelegramQueryServerBackgroundService> logger,
    IInMemoryCacheLoader inMemoryCacheLoader,
    ILanguageCacheService languageCacheService,
    IUserStatusCacheAppService userStatusCacheAppService,
    IBotVerificationCache botVerificationCache)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Query server starting...");
        await inMemoryCacheLoader.LoadAsync();
        await languageCacheService.LoadAllLanguagesAsync();
        await languageCacheService.LoadAllLanguageTextAsync();
        await userStatusCacheAppService.LoadFromDatabaseAsync();
        // Converting a user or a channel reads the verification badge synchronously, so the snapshot
        // has to exist before the first request rather than after the first async conversion.
        await botVerificationCache.EnsureFreshAsync(stoppingToken);

        logger.LogInformation("Query server started");
    }
}
