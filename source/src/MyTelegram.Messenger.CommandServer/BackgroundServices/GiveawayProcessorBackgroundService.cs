using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.CommandServer.BackgroundServices;

/// <summary>
/// Background service that automatically processes and completes expired giveaways.
/// Schedules timers for each active giveaway to complete them exactly at UntilDate.
/// </summary>
public class GiveawayProcessorBackgroundService : BackgroundService
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<GiveawayProcessorBackgroundService> _logger;
    private readonly Dictionary<string, Timer> _giveawayTimers = new();
    private readonly object _timersLock = new();

    public GiveawayProcessorBackgroundService(
        IMongoDatabase mongoDatabase,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<GiveawayProcessorBackgroundService> logger)
    {
        _mongoDatabase = mongoDatabase;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _logger.LogInformation("GiveawayProcessorBackgroundService constructor called");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Giveaway processor service started");

        // Initial scan for existing active giveaways
        await ScheduleActiveGiveawaysAsync(stoppingToken);

        // Periodic check every 1 minute for new giveaways
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                await ScheduleActiveGiveawaysAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in giveaway processor loop");
            }
        }

        // Cleanup timers on shutdown
        lock (_timersLock)
        {
            foreach (var timer in _giveawayTimers.Values)
            {
                timer.Dispose();
            }
            _giveawayTimers.Clear();
        }

        _logger.LogInformation("Giveaway processor service stopped");
    }

    private async Task ScheduleActiveGiveawaysAsync(CancellationToken cancellationToken)
    {
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var giveawayCol = _mongoDatabase.GetCollection<BsonDocument>("giveaways");

        // Find active giveaways
        var filter = Builders<BsonDocument>.Filter.Eq("Status", "active");
        var activeGiveaways = await giveawayCol.Find(filter).ToListAsync(cancellationToken);

        foreach (var giveaway in activeGiveaways)
        {
            var giveawayId = giveaway["_id"].AsString;
            var untilDate = giveaway["UntilDate"].AsInt32;

            // Skip if already scheduled
            lock (_timersLock)
            {
                if (_giveawayTimers.ContainsKey(giveawayId))
                    continue;
            }

            // Calculate delay until completion
            var delay = TimeSpan.FromSeconds(untilDate - currentTime);

            if (delay.TotalSeconds <= 0)
            {
                // Already expired, complete immediately
                _logger.LogInformation("Giveaway {GiveawayId} already expired, completing now", giveawayId);
                _ = Task.Run(() => CompleteGiveawayAsync(giveaway, CancellationToken.None), cancellationToken);
            }
            else if (delay.TotalDays < 7) // Only schedule timers for giveaways within 7 days
            {
                // Schedule timer
                var timer = new Timer(
                    async _ => await CompleteGiveawayAsync(giveaway, CancellationToken.None),
                    null,
                    delay,
                    Timeout.InfiniteTimeSpan
                );

                lock (_timersLock)
                {
                    _giveawayTimers[giveawayId] = timer;
                }

                _logger.LogInformation("Scheduled giveaway {GiveawayId} to complete in {Delay}",
                    giveawayId, delay);
            }
        }
    }

    private async Task CompleteGiveawayAsync(BsonDocument giveaway, CancellationToken cancellationToken)
    {
        var giveawayId = giveaway["_id"].AsString;
        var channelId = giveaway["ChannelId"].AsInt64;
        var msgId = giveaway.Contains("MsgId") ? giveaway["MsgId"].AsInt32 : 0;
        var type = giveaway["Type"].AsString;
        var winnersCount = giveaway["Winners"].AsInt32;
        var onlyNewSubscribers = giveaway.Contains("OnlyNewSubscribers") && giveaway["OnlyNewSubscribers"].AsBoolean;

        _logger.LogInformation("Completing giveaway {GiveawayId}: channel={ChannelId} msgId={MsgId} type={Type} winners={Winners}",
            giveawayId, channelId, msgId, type, winnersCount);

        // Remove timer if exists
        lock (_timersLock)
        {
            if (_giveawayTimers.TryGetValue(giveawayId, out var timer))
            {
                timer.Dispose();
                _giveawayTimers.Remove(giveawayId);
            }
        }

        // Get channel participants
        var participantCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var channel = await participantCol.Find(channelFilter).FirstOrDefaultAsync(cancellationToken);

        if (channel == null)
        {
            _logger.LogWarning("Channel {ChannelId} not found for giveaway", channelId);
            return;
        }

        // Get channel members
        var memberCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");

        // Build list of channels to check (main channel + additional channels)
        var channelIds = new List<long> { channelId };
        if (giveaway.Contains("AdditionalChannels") && giveaway["AdditionalChannels"].IsBsonArray)
        {
            var additionalChannels = giveaway["AdditionalChannels"].AsBsonArray
                .Select(x => x.AsInt64)
                .ToList();
            channelIds.AddRange(additionalChannels);
        }

        // Find users who are members of ALL required channels
        var eligibleUserIds = new HashSet<long>();
        bool firstChannel = true;

        foreach (var checkChannelId in channelIds)
        {
            var memberFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", checkChannelId),
                Builders<BsonDocument>.Filter.Eq("Left", false)
            );

            // If only new subscribers, filter by join date
            if (onlyNewSubscribers)
            {
                var startDate = giveaway["StartDate"].AsInt32;
                memberFilter = Builders<BsonDocument>.Filter.And(
                    memberFilter,
                    Builders<BsonDocument>.Filter.Gte("JoinDate", startDate)
                );
            }

            var members = await memberCol.Find(memberFilter).ToListAsync(cancellationToken);
            var channelUserIds = members.Select(m => m["UserId"].AsInt64).ToHashSet();

            if (firstChannel)
            {
                eligibleUserIds = channelUserIds;
                firstChannel = false;
            }
            else
            {
                // Intersect - user must be in ALL channels
                eligibleUserIds.IntersectWith(channelUserIds);
            }

            _logger.LogInformation("Channel {ChannelId} has {Count} eligible members",
                checkChannelId, channelUserIds.Count);
        }

        // Filter by countries if specified
        if (giveaway.Contains("Countries") && giveaway["Countries"].IsBsonArray)
        {
            var countries = giveaway["Countries"].AsBsonArray
                .Select(x => x.AsString)
                .ToList();

            if (countries.Count > 0)
            {
                var userCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
                var countryFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.In("UserId", eligibleUserIds),
                    Builders<BsonDocument>.Filter.In("CountryCode", countries)
                );
                var usersInCountries = await userCol.Find(countryFilter).ToListAsync(cancellationToken);
                var countryUserIds = usersInCountries.Select(u => u["UserId"].AsInt64).ToHashSet();

                eligibleUserIds.IntersectWith(countryUserIds);

                _logger.LogInformation("After country filter ({Countries}): {Count} eligible users",
                    string.Join(", ", countries), eligibleUserIds.Count);
            }
        }

        if (eligibleUserIds.Count == 0)
        {
            _logger.LogWarning("No eligible participants for giveaway in channel {ChannelId}", channelId);

            // Mark as finished with no winners
            var giveawayCollection = _mongoDatabase.GetCollection<BsonDocument>("giveaways");
            await giveawayCollection.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", giveaway["_id"]),
                Builders<BsonDocument>.Update
                    .Set("Status", "finished")
                    .Set("WinnerUserIds", new BsonArray())
                    .Set("FinishedAt", DateTime.UtcNow),
                cancellationToken: cancellationToken
            );
            return;
        }

        // Randomly select winners
        var random = new Random();
        var eligibleList = eligibleUserIds.ToList();
        var winners = new List<long>();

        var actualWinnersCount = Math.Min(winnersCount, eligibleList.Count);
        for (int i = 0; i < actualWinnersCount; i++)
        {
            var index = random.Next(eligibleList.Count);
            winners.Add(eligibleList[index]);
            eligibleList.RemoveAt(index); // Avoid duplicates
        }

        _logger.LogInformation("Selected {Count} winners from {Total} eligible participants",
            winners.Count, eligibleUserIds.Count);

        // Create scope to get IMessageAppService
        using var scope = _serviceScopeFactory.CreateScope();
        var messageAppService = scope.ServiceProvider.GetRequiredService<IMessageAppService>();

        var months = type == "premium" && giveaway.Contains("Months") ? giveaway["Months"].AsInt32 : 12;
        var starsAmount = type == "stars" && giveaway.Contains("Stars") ? giveaway["Stars"].AsInt64 : 0;

        // Get giveaway creator
        var createdBy = giveaway.Contains("CreatedBy") && !giveaway["CreatedBy"].IsBsonNull
            ? giveaway["CreatedBy"].AsInt64
            : 0L;

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var unclaimedCount = 0;

        if (type == "stars")
        {
            // Stars giveaway - send messageActionPrizeStars directly, no gift codes
            var starsCol = _mongoDatabase.GetCollection<BsonDocument>("star-balances");
            var transCol = _mongoDatabase.GetCollection<BsonDocument>("star-transactions");

            foreach (var winnerId in winners)
            {
                // Add stars to winner's balance
                await starsCol.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", $"balance-{winnerId}"),
                    Builders<BsonDocument>.Update.Inc("Balance", starsAmount),
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken: cancellationToken
                );

                // Create transaction
                await transCol.InsertOneAsync(new BsonDocument
                {
                    ["_id"] = $"trans-{Guid.NewGuid()}",
                    ["UserId"] = winnerId,
                    ["Amount"] = starsAmount,
                    ["Date"] = now,
                    ["Gift"] = true,
                    ["Title"] = "Giveaway Prize"
                }, cancellationToken: cancellationToken);

                // Send messageActionPrizeStars to winner
                var prizeAction = new TMessageActionPrizeStars
                {
                    Stars = starsAmount,
                    TransactionId = $"giveaway-{channelId}-{msgId}-{winnerId}",
                    BoostPeer = new TPeerChannel { ChannelId = channelId },
                    GiveawayMsgId = msgId
                };

                var sendInput = new SendMessageInput(
                    new RequestInfo(
                        ConnectionId: string.Empty,
                        SessionId: 0,
                        ReqMsgId: 0,
                        UserId: MyTelegramConsts.NotificationServiceUserId,
                        AccessHashKeyId: 0,
                        AuthKeyId: 0,
                        PermAuthKeyId: 0,
                        RequestId: Guid.NewGuid(),
                        Layer: 222,
                        Date: DateTime.UtcNow.ToTimestamp(),
                        DeviceType: DeviceType.Android
                    ),
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, winnerId),
                    string.Empty,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: prizeAction
                );

                await messageAppService.SendMessageAsync([sendInput]);
            }

            _logger.LogInformation("Distributed {Amount} stars to {Count} winners",
                starsAmount, winners.Count);
        }
        else
        {
            // Premium giveaway - check if winners already have Premium
            var subCol = _mongoDatabase.GetCollection<BsonDocument>("premium_subscriptions");
            var giftCodeCol = _mongoDatabase.GetCollection<BsonDocument>("premium_gift_codes");

            foreach (var winnerId in winners)
            {
                // Check if winner has active Premium
                var existingSub = await subCol.Find(
                    Builders<BsonDocument>.Filter.Eq("_id", $"premium-{winnerId}")
                ).FirstOrDefaultAsync(cancellationToken);

                bool hasActivePremium = existingSub != null &&
                    existingSub.Contains("Active") && existingSub["Active"].AsBoolean &&
                    existingSub.Contains("ExpiresAt") && existingSub["ExpiresAt"].AsInt32 > now;

                var slug = $"giveaway-{channelId}-{msgId}-{winnerId}-{Guid.NewGuid():N}";

                // Create gift code
                await giftCodeCol.InsertOneAsync(new BsonDocument
                {
                    ["_id"] = $"giftcode-{slug}",
                    ["Slug"] = slug,
                    ["ToId"] = winnerId,
                    ["FromId"] = createdBy,
                    ["GiveawayMsgId"] = msgId,
                    ["ChannelId"] = channelId,
                    ["Months"] = months,
                    ["Used"] = false,
                    ["Unclaimed"] = hasActivePremium, // Mark as unclaimed if user has Premium
                    ["CreatedAt"] = DateTime.UtcNow,
                    ["ViaGiveaway"] = true
                }, cancellationToken: cancellationToken);

                if (hasActivePremium)
                {
                    unclaimedCount++;
                }

                // Send messageActionGiftCode to winner
                var messageAction = new TMessageActionGiftCode
                {
                    ViaGiveaway = true,
                    Unclaimed = hasActivePremium, // Set unclaimed flag
                    BoostPeer = new TPeerChannel { ChannelId = channelId },
                    Days = months * 30,
                    Slug = slug
                };

                var sendInput = new SendMessageInput(
                    new RequestInfo(
                        ConnectionId: string.Empty,
                        SessionId: 0,
                        ReqMsgId: 0,
                        UserId: MyTelegramConsts.NotificationServiceUserId,
                        AccessHashKeyId: 0,
                        AuthKeyId: 0,
                        PermAuthKeyId: 0,
                        RequestId: Guid.NewGuid(),
                        Layer: 222,
                        Date: DateTime.UtcNow.ToTimestamp(),
                        DeviceType: DeviceType.Android
                    ),
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, winnerId),
                    string.Empty,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: messageAction
                );

                await messageAppService.SendMessageAsync([sendInput]);
            }

            _logger.LogInformation("Created {Count} gift codes for winners, {Unclaimed} unclaimed (users already have Premium)",
                winners.Count, unclaimedCount);
        }

        // Update giveaway status to finished
        var giveawayCol = _mongoDatabase.GetCollection<BsonDocument>("giveaways");
        await giveawayCol.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", giveaway["_id"]),
            Builders<BsonDocument>.Update
                .Set("Status", "finished")
                .Set("WinnerUserIds", new BsonArray(winners))
                .Set("UnclaimedCount", unclaimedCount)
                .Set("FinishedAt", DateTime.UtcNow),
            cancellationToken: cancellationToken
        );

        // Add 4 boosts to channel for each Premium subscription given away (only for claimed codes)
        if (type == "premium" && winners.Count > 0)
        {
            var claimedCount = winners.Count - unclaimedCount;
            if (claimedCount > 0)
            {
                var boostCol = _mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
                var boostDocs = new List<BsonDocument>();

                for (int i = 0; i < claimedCount; i++)
                {
                    // Each Premium subscription gives 4 boosts to the channel
                    // These boosts are tied to the giveaway and last for the subscription duration
                    var boostId = $"giveaway-boost-{channelId}-{msgId}-{i}";
                    var expiresAt = DateTime.UtcNow.AddMonths(months);

                    boostDocs.Add(new BsonDocument
                    {
                        ["_id"] = boostId,
                        ["ChannelId"] = channelId,
                        ["UserId"] = 0, // System boost, not from a user
                        ["Slot"] = 0,
                        ["Multiplier"] = 4, // 4 boosts per Premium subscription
                        ["Date"] = DateTime.UtcNow,
                        ["Expires"] = expiresAt,
                        ["Source"] = "giveaway",
                        ["GiveawayId"] = giveaway["_id"].AsString
                    });
                }

                if (boostDocs.Count > 0)
                {
                    await boostCol.InsertManyAsync(boostDocs, cancellationToken: cancellationToken);
                    _logger.LogInformation("Added {Count} boosts (4 per claimed winner) to channel {ChannelId} from giveaway",
                        boostDocs.Count, channelId);
                }
            }
        }

        // Send giveaway results to channel
        var winnersAreVisible = giveaway.Contains("WinnersAreVisible") && giveaway["WinnersAreVisible"].AsBoolean;
        var additionalChannelsCount = giveaway.Contains("AdditionalChannels") && giveaway["AdditionalChannels"].IsBsonArray
            ? giveaway["AdditionalChannels"].AsBsonArray.Count
            : 0;

        if (winnersAreVisible)
        {
            // Send message with messageMediaGiveawayResults (includes winners list)
            var giveawayResultsMedia = new TMessageMediaGiveawayResults
            {
                OnlyNewSubscribers = onlyNewSubscribers,
                ChannelId = channelId,
                AdditionalPeersCount = additionalChannelsCount > 0 ? additionalChannelsCount : null,
                LaunchMsgId = msgId,
                WinnersCount = winners.Count,
                UnclaimedCount = unclaimedCount,
                Winners = new TVector<long>(winners),
                Months = type == "premium" ? months : null,
                Stars = type == "stars" ? starsAmount : null,
                UntilDate = giveaway["UntilDate"].AsInt32
            };

            var channelResultInput = new SendMessageInput(
                new RequestInfo(
                    ConnectionId: string.Empty,
                    SessionId: 0,
                    ReqMsgId: 0,
                    UserId: createdBy,
                    AccessHashKeyId: 0,
                    AuthKeyId: 0,
                    PermAuthKeyId: 0,
                    RequestId: Guid.NewGuid(),
                    Layer: 222,
                    Date: DateTime.UtcNow.ToTimestamp(),
                    DeviceType: DeviceType.Android
                ),
                createdBy,
                new Peer(PeerType.Channel, channelId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.Media,
                messageType: MessageType.Text,
                media: giveawayResultsMedia
            );

            await messageAppService.SendMessageAsync([channelResultInput]);

            _logger.LogInformation("Giveaway completed: {Winners} winners selected, {Unclaimed} unclaimed. Results message with winners list sent to channel.",
                winners.Count, unclaimedCount);
        }
        else
        {
            // Send message with messageActionGiveawayResults (no winners list)
            var giveawayResultsAction = new TMessageActionGiveawayResults
            {
                Stars = type == "stars",
                WinnersCount = winners.Count,
                UnclaimedCount = unclaimedCount
            };

            var channelResultInput = new SendMessageInput(
                new RequestInfo(
                    ConnectionId: string.Empty,
                    SessionId: 0,
                    ReqMsgId: 0,
                    UserId: MyTelegramConsts.NotificationServiceUserId,
                    AccessHashKeyId: 0,
                    AuthKeyId: 0,
                    PermAuthKeyId: 0,
                    RequestId: Guid.NewGuid(),
                    Layer: 222,
                    Date: DateTime.UtcNow.ToTimestamp(),
                    DeviceType: DeviceType.Android
                ),
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.Channel, channelId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: giveawayResultsAction
            );

            await messageAppService.SendMessageAsync([channelResultInput]);

            _logger.LogInformation("Giveaway completed: {Winners} winners selected, {Unclaimed} unclaimed. Results message sent to channel (winners hidden).",
                winners.Count, unclaimedCount);
        }
    }
}
