using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Fires the schedule queues: sends the messages whose time has come and the ones waiting for their
/// peer to come online.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public class ScheduledMessageSender(
    IServiceScopeFactory serviceScopeFactory,
    IMongoDatabase mongoDatabase,
    ILogger<ScheduledMessageSender> logger)
    : BackgroundService
{
    /// <summary>
    /// How long a claimed entry stays reserved for this process.
    /// </summary>
    private const int LeaseSeconds = 60;

    private const int BatchSize = 100;

    /// <summary>
    /// The loop never sleeps longer than this, so newly queued, re-scheduled and when-online entries are
    /// picked up quickly.
    /// </summary>
    private static readonly TimeSpan MaxIdleDelay = TimeSpan.FromSeconds(10);

    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ScheduledMessageSender started");

        await EnsureIndexesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(await GetIdleDelayAsync(stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing scheduled messages");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("ScheduledMessageSender stopped");
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>()
                .EnsureIndexesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the indexes of the scheduled messages collection");
        }
    }

    private async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IScheduledMessageDispatcher>();

        var now = DateTime.UtcNow.ToTimestamp();
        var documents = await store.ClaimDueAsync(now, BatchSize, LeaseSeconds, cancellationToken);

        var onlineUserIds = await GetOnlineUserIdsAsync(cancellationToken);
        documents.AddRange(await store.ClaimWhenOnlineAsync(onlineUserIds, BatchSize, LeaseSeconds,
            cancellationToken));

        if (documents.Count == 0)
        {
            return 0;
        }

        logger.LogInformation("Flushing {Count} scheduled messages", documents.Count);

        // Failures are isolated per group: one broken message must not hold up the rest of the queue, and
        // it must not be retried in a tight loop either. An album stays one group so its parts are still
        // sent together.
        foreach (var batch in BuildBatches(documents))
        {
            try
            {
                await dispatcher.FlushAsync(batch);
            }
            catch (Exception ex)
            {
                foreach (var document in batch)
                {
                    var nextAttemptDate = now + Math.Min(60 * (int)Math.Pow(2, document.Attempts), 3600);
                    await store.ReleaseAsync(document, nextAttemptDate);

                    if (document.Attempts + 1 >= MaxAttempts)
                    {
                        logger.LogError(ex,
                            "Scheduled message {Id} failed {Attempts} times, next attempt at {NextAttemptDate}",
                            document.Id, document.Attempts + 1, nextAttemptDate);
                    }
                    else
                    {
                        logger.LogWarning(ex,
                            "Could not send scheduled message {Id}, next attempt at {NextAttemptDate}",
                            document.Id, nextAttemptDate);
                    }
                }
            }
        }

        return documents.Count;
    }

    /// <summary>
    /// Album parts must be sent in one go, everything else is flushed on its own so a single broken
    /// message cannot take the rest of the batch down with it.
    /// </summary>
    private static List<List<ScheduledMessageDocument>> BuildBatches(List<ScheduledMessageDocument> documents)
    {
        var batches = new List<List<ScheduledMessageDocument>>();
        foreach (var group in documents.GroupBy(p => new { p.SenderUserId, p.PeerId, p.PeerType, p.Item.GroupId }))
        {
            if (group.Key.GroupId.HasValue)
            {
                batches.Add(group.ToList());
                continue;
            }

            batches.AddRange(group.Select(p => new List<ScheduledMessageDocument> { p }));
        }

        return batches;
    }

    /// <summary>
    /// Users that are online right now, for the entries scheduled with the special "when online" date.
    /// </summary>
    private async Task<List<long>> GetOnlineUserIdsAsync(CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddSeconds(-OnlineWindowSeconds);
        var collection = mongoDatabase.GetCollection<UserStatusMongoModel>("user_status");

        var statuses = await collection
            .Find(Builders<UserStatusMongoModel>.Filter.And(
                Builders<UserStatusMongoModel>.Filter.Eq(p => p.Online, true),
                Builders<UserStatusMongoModel>.Filter.Gte(p => p.LastOnline, since)))
            .Project(Builders<UserStatusMongoModel>.Projection.Include(p => p.UserId))
            .ToListAsync(cancellationToken);

        return statuses.Select(p => p["UserId"].AsInt64).ToList();
    }

    /// <summary>
    /// Same window the user status cache uses to consider a presence report still valid.
    /// </summary>
    private const int OnlineWindowSeconds = 90;

    private async Task<TimeSpan> GetIdleDelayAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduledMessageStore>();

        var nextScheduleDate = await store.GetNextScheduleDateAsync(cancellationToken);
        if (!nextScheduleDate.HasValue)
        {
            return MaxIdleDelay;
        }

        var delay = TimeSpan.FromSeconds(nextScheduleDate.Value - DateTime.UtcNow.ToTimestamp());

        // A due entry that could not be claimed belongs to another command server or is waiting out its
        // retry backoff, so there is nothing to gain from spinning on it.
        return delay <= TimeSpan.Zero || delay > MaxIdleDelay ? MaxIdleDelay : delay;
    }
}
