using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Background service that automatically archives expired stories.
/// Runs every 5 minutes to move stories past their expiration date to the archive.
/// </summary>
public class StoryArchiveBackgroundService(
    IMongoDatabase mongoDatabase,
    ILogger<StoryArchiveBackgroundService> logger)
    : BackgroundService
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Story archive service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArchiveExpiredStoriesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error archiving expired stories");
            }

            // Run every 5 minutes
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        logger.LogInformation("Story archive service stopped");
    }

    private async Task ArchiveExpiredStoriesAsync(CancellationToken cancellationToken)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Find stories that have expired but are not yet archived or deleted
        var filter = Builders<StoryDocument>.Filter.And(
            Builders<StoryDocument>.Filter.Lte(s => s.ExpireDate, currentTime),
            Builders<StoryDocument>.Filter.Eq(s => s.Archived, false),
            Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)
        );

        var update = Builders<StoryDocument>.Update.Set(s => s.Archived, true);

        var result = await _storyCollection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken);

        if (result.ModifiedCount > 0)
        {
            logger.LogInformation("Archived {Count} expired stories", result.ModifiedCount);
        }
    }
}
