using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

public class MyTelegramQueryServerHandlerBackgroundService(
    ILogger<MyTelegramQueryServerHandlerBackgroundService> logger,
    IHandlerHelper handlerHelper,
    IInMemoryCacheLoader inMemoryCacheLoader)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Query server handler initialization starting...");
        handlerHelper.InitAllHandlers();
        await inMemoryCacheLoader.LoadAsync();

        logger.LogInformation("Query server handlers initialized");
    }
}
