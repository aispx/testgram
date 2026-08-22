using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Executes the two deletions nobody is waiting on an rpc for: a deletion that was delayed by a
/// week because the 2FA password was not provided (https://corefork.telegram.org/api/account-deletion),
/// and the self-destruction of an account that has not come online for longer than its
/// <c>account.setAccountTTL</c> period.
/// </summary>
public class AccountDeletionBackgroundService(
    IAccountDeletionService accountDeletionService,
    IMongoDatabase database,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<AccountDeletionBackgroundService> logger) : BackgroundService
{
    /// <summary>Matches the value <c>UserAggregate</c> stores when an account is created.</summary>
    private const int DefaultAccountTtlDays = 365;

    /// <summary>The shortest period account.setAccountTTL accepts, used to pre-filter candidates.</summary>
    private const int MinAccountTtlDays = 30;

    /// <summary>
    /// How long one pass may hold a pending deletion. Long enough for the commands to go through,
    /// short enough that a crashed pass does not park the deletion for another day.
    /// </summary>
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Account deletion worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = options.CurrentValue.AccountDeletion;
            if (config.Enabled)
            {
                try
                {
                    await ExecuteDuePendingDeletionsAsync(stoppingToken);

                    if (config.SelfDestructEnabled)
                    {
                        await ExecuteSelfDestructAsync(config.SelfDestructBatchSize, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Account deletion worker error");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, config.SweepIntervalSeconds)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Account deletion worker stopped");
    }

    private async Task ExecuteDuePendingDeletionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pending = await accountDeletionService.ClaimNextDuePendingAsync(DateTime.UtcNow, ClaimDuration,
                cancellationToken);
            if (pending == null)
            {
                return;
            }

            logger.LogInformation("Executing delayed deletion of account {UserId}", pending.UserId);
            await accountDeletionService.DeleteAccountAsync(pending.UserId, pending.Reason,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Deletes accounts whose owner stopped coming online for longer than the period they picked in
    /// <c>account.setAccountTTL</c>. An account that never reported presence is measured from its
    /// creation date instead, otherwise it would never expire.
    /// </summary>
    private async Task ExecuteSelfDestructAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var earliestPossibleThreshold = now.AddDays(-MinAccountTtlDays);

        // The creation date pre-filter keeps the scan small; accounts predating the field (it is not
        // written by older read models) carry no date and are therefore never self-destructed - a
        // missed deletion is the safe direction to err in.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Ne("IsDeleted", true),
            Builders<BsonDocument>.Filter.Ne("Bot", true),
            Builders<BsonDocument>.Filter.Ne("Support", true),
            Builders<BsonDocument>.Filter.Lt("CreationTime", earliestPossibleThreshold));

        var candidates = await database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(filter)
            .Project(Builders<BsonDocument>.Projection
                .Include("UserId")
                .Include("AccountTtl")
                .Include("CreationTime"))
            .Limit(batchSize)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        var userIds = candidates
            .Where(p => p.Contains("UserId") && !p["UserId"].IsBsonNull)
            .Select(p => p["UserId"].ToInt64())
            .ToList();

        var statuses = await database.GetCollection<UserStatusMongoModel>("user_status")
            .Find(Builders<UserStatusMongoModel>.Filter.In(p => p.UserId, userIds))
            .ToListAsync(cancellationToken);
        var lastOnlineByUserId = statuses.ToDictionary(p => p.UserId, p => p.LastOnline);

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!candidate.Contains("UserId") || candidate["UserId"].IsBsonNull)
            {
                continue;
            }

            var userId = candidate["UserId"].ToInt64();

            // Bots and accounts flagged as support are already filtered out above; the built-in
            // service users carry no such flag, so they are skipped by id.
            if (PeerKindHelper.IsSystemUserId(userId))
            {
                continue;
            }

            var ttlDays = candidate.Contains("AccountTtl") && !candidate["AccountTtl"].IsBsonNull
                ? candidate["AccountTtl"].ToInt32()
                : 0;
            if (ttlDays <= 0)
            {
                ttlDays = DefaultAccountTtlDays;
            }

            var lastActivity = lastOnlineByUserId.TryGetValue(userId, out var lastOnline)
                ? lastOnline
                : GetCreationTime(candidate);
            if (lastActivity == null || lastActivity.Value > now.AddDays(-ttlDays))
            {
                continue;
            }

            logger.LogInformation(
                "Self destructing account {UserId}: inactive since {LastActivity}, ttl {TtlDays} days",
                userId, lastActivity, ttlDays);

            await accountDeletionService.DeleteAccountAsync(userId, "account self-destruction",
                cancellationToken: cancellationToken);
        }
    }

    private static DateTime? GetCreationTime(BsonDocument document)
    {
        if (!document.Contains("CreationTime") || document["CreationTime"].IsBsonNull)
        {
            return null;
        }

        return document["CreationTime"].ToUniversalTime();
    }
}
