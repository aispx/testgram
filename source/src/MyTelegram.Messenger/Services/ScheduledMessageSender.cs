using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services;

public class ScheduledMessageSender : BackgroundService
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IMessageAppService _messageAppService;
    private readonly ILogger<ScheduledMessageSender> _logger;

    public ScheduledMessageSender(
        IMongoDatabase mongoDatabase,
        IMessageAppService messageAppService,
        ILogger<ScheduledMessageSender> logger)
    {
        _mongoDatabase = mongoDatabase;
        _messageAppService = messageAppService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledMessageSender started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextScheduleTime = await GetNextScheduleTimeAsync(stoppingToken);

                if (nextScheduleTime.HasValue)
                {
                    var delay = nextScheduleTime.Value - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        _logger.LogDebug("Next scheduled message at {Time}, waiting {Delay}s",
                            nextScheduleTime.Value, delay.TotalSeconds);
                        await Task.Delay(delay, stoppingToken);
                    }
                }
                else
                {
                    // No scheduled messages, check again in 30 seconds
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await ProcessScheduledMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scheduled messages");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("ScheduledMessageSender stopped");
    }

    private async Task<DateTimeOffset?> GetNextScheduleTimeAsync(CancellationToken cancellationToken)
    {
        var collection = _mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");
        var doc = await collection.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("ScheduleDate"))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        if (doc == null)
            return null;

        var scheduleDate = doc["ScheduleDate"].AsInt32;
        return DateTimeOffset.FromUnixTimeSeconds(scheduleDate);
    }

    private async Task ProcessScheduledMessagesAsync(CancellationToken cancellationToken)
    {
        var collection = _mongoDatabase.GetCollection<BsonDocument>("scheduled_messages");
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Find messages that should be sent now
        var filter = Builders<BsonDocument>.Filter.Lte("ScheduleDate", currentTime);
        var docs = await collection.Find(filter).ToListAsync(cancellationToken);

        if (docs.Count == 0)
            return;

        _logger.LogInformation("Found {Count} scheduled messages to send", docs.Count);

        // Group by sender and peer
        var groupedMessages = docs.GroupBy(d => new
        {
            SenderUserId = d["SenderUserId"].AsInt64,
            PeerId = d["PeerId"].AsInt64,
            PeerType = d["PeerType"].AsString
        });

        foreach (var group in groupedMessages)
        {
            try
            {
                var peerType = Enum.Parse<PeerType>(group.Key.PeerType);
                var peer = new Peer(peerType, group.Key.PeerId);

                var sendInputs = new List<SendMessageInput>();
                var scheduledIds = new List<int>();

                foreach (var doc in group)
                {
                    var entities = doc.Contains("Entities") && !doc["Entities"].IsBsonNull
                        ? System.Text.Json.JsonSerializer.Deserialize<TVector<IMessageEntity>>(doc["Entities"].AsString)
                        : null;

                    var sendInput = new SendMessageInput(
                        new RequestInfo(
                            ConnectionId: "scheduled",
                            SessionId: 0,
                            ReqMsgId: 0,
                            UserId: group.Key.SenderUserId,
                            AccessHashKeyId: 0,
                            AuthKeyId: 0,
                            PermAuthKeyId: 0,
                            RequestId: Guid.NewGuid(),
                            Layer: 222,
                            Date: DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        ),
                        group.Key.SenderUserId,
                        peer,
                        doc["Message"].AsString,
                        doc["RandomId"].AsInt64,
                        entities: entities,
                        sendMessageType: SendMessageType.Text,
                        messageType: MessageType.Text,
                        silent: doc.Contains("Silent") && doc["Silent"].AsBoolean,
                        noForwards: doc.Contains("NoForwards") && doc["NoForwards"].AsBoolean
                    );

                    sendInputs.Add(sendInput);
                    scheduledIds.Add(doc["ScheduledMessageId"].AsInt32);
                }

                // Send messages
                await _messageAppService.SendMessageAsync(sendInputs);

                // Delete from scheduled queue
                var deleteFilter = Builders<BsonDocument>.Filter.In("ScheduledMessageId", scheduledIds);
                await collection.DeleteManyAsync(deleteFilter, cancellationToken);

                _logger.LogInformation("Sent {Count} scheduled messages for user {UserId} to peer {PeerId}",
                    scheduledIds.Count, group.Key.SenderUserId, group.Key.PeerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending scheduled messages for user {UserId}",
                    group.Key.SenderUserId);
            }
        }
    }
}
