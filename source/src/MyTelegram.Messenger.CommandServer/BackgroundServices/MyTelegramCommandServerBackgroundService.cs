using Microsoft.Extensions.Hosting;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

public class MyTelegramCommandServerBackgroundService(
    ILogger<MyTelegramCommandServerBackgroundService> logger,
    IHandlerHelper handlerHelper,
    IInMemoryCacheLoader inMemoryCacheLoader,
    IBotVerificationCache botVerificationCache)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Command server starting...");
        handlerHelper.InitAllHandlers();
        await inMemoryCacheLoader.LoadAsync();
        // Converting a user or a channel reads the verification badge synchronously, so the snapshot
        // has to exist before the first request rather than after the first async conversion.
        await botVerificationCache.EnsureFreshAsync(stoppingToken);

        logger.LogInformation("Command server started");
    }
}
