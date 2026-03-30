using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

public class MyTelegramQueryServerBackgroundService(
    ILogger<MyTelegramQueryServerBackgroundService> logger,
    IInMemoryCacheLoader inMemoryCacheLoader,
    ILanguageCacheService languageCacheService,
    IUserStatusCacheAppService userStatusCacheAppService)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Query server starting...");
        await inMemoryCacheLoader.LoadAsync();
        await languageCacheService.LoadAllLanguagesAsync();
        await languageCacheService.LoadAllLanguageTextAsync();
        await userStatusCacheAppService.LoadFromDatabaseAsync();

        logger.LogInformation("Query server started");
    }
}
