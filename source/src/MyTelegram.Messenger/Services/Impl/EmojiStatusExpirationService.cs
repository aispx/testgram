using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Clears <a href="https://core.telegram.org/api/emoji-status">emoji statuses</a> whose <c>until</c>
/// has passed. Converters already hide an expired status, but going through the aggregate is what
/// actually drops it from the stored state and pushes <c>updateUserEmojiStatus</c> to the clients.
/// </summary>
public class EmojiStatusExpirationService(
    IMongoDatabase database,
    ICommandBus commandBus,
    IUserAppService userAppService,
    ILogger<EmojiStatusExpirationService> logger) : BackgroundService
{
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Emoji status expiration service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ClearExpiredUserStatusesAsync(stoppingToken);
                await ClearExpiredChannelStatusesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in emoji status expiration service");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        logger.LogInformation("Emoji status expiration service stopped");
    }

    private async Task ClearExpiredUserStatusesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = await database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Ne("EmojiStatusValidUntil", BsonNull.Value),
                Builders<BsonDocument>.Filter.Lte("EmojiStatusValidUntil", now)))
            .Project(Builders<BsonDocument>.Projection.Include("UserId"))
            .ToListAsync(cancellationToken);

        foreach (var doc in expired)
        {
            if (!doc.TryGetValue("UserId", out var value) || value.IsBsonNull)
            {
                continue;
            }

            var userId = value.ToInt64();
            await commandBus.PublishAsync(new UpdateEmojiStatusCommand(
                UserId.Create(userId),
                CreateRequestInfo(userId),
                null), cancellationToken);
            userAppService.InvalidateCache(userId);

            logger.LogInformation("Cleared expired emoji status of user {UserId}", userId);
        }
    }

    private async Task ClearExpiredChannelStatusesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = await database.GetCollection<BsonDocument>("eventflow-channelreadmodel")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Ne("EmojiStatus", BsonNull.Value),
                Builders<BsonDocument>.Filter.Ne("EmojiStatus.Until", BsonNull.Value),
                Builders<BsonDocument>.Filter.Lte("EmojiStatus.Until", now)))
            .Project(Builders<BsonDocument>.Projection.Include("ChannelId").Include("CreatorId"))
            .ToListAsync(cancellationToken);

        foreach (var doc in expired)
        {
            if (!doc.TryGetValue("ChannelId", out var channelIdValue) || channelIdValue.IsBsonNull)
            {
                continue;
            }

            var channelId = channelIdValue.ToInt64();
            // The aggregate checks admin rights, so the expiry is attributed to the channel creator.
            var creatorId = doc.TryGetValue("CreatorId", out var creator) && !creator.IsBsonNull
                ? creator.ToInt64()
                : 0;
            if (creatorId == 0)
            {
                logger.LogWarning("Cannot clear expired emoji status of channel {ChannelId}: no creator", channelId);
                continue;
            }

            await commandBus.PublishAsync(new UpdateChannelEmojiStatusCommand(
                ChannelId.Create(channelId),
                CreateRequestInfo(creatorId),
                null), cancellationToken);

            logger.LogInformation("Cleared expired emoji status of channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// Commands carrying a <see cref="RequestInfo"/> are deduplicated on their <c>ReqMsgId</c>, so a
    /// fixed one (such as <see cref="RequestInfo.Empty"/>) would make every expiry after the first be
    /// rejected as a duplicate operation. There is no client request behind these, so a fresh id is
    /// generated per command; <c>ReqMsgId</c> stays non-zero only to keep them distinct, and no rpc
    /// result is sent because the id belongs to no pending request.
    /// </summary>
    private static RequestInfo CreateRequestInfo(long userId)
    {
        return RequestInfo.Empty with
        {
            UserId = userId,
            ReqMsgId = DateTime.UtcNow.Ticks,
            RequestId = Guid.NewGuid(),
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }
}
