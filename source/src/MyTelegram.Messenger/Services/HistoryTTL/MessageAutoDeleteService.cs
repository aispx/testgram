using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.HistoryTTL;

public class MessageAutoDeleteService : BackgroundService
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<MessageAutoDeleteService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(10);

    public MessageAutoDeleteService(
        IMongoDatabase database,
        ILogger<MessageAutoDeleteService> logger)
    {
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Message Auto-Delete Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Message Auto-Delete Service");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        _logger.LogInformation("Message Auto-Delete Service stopped");
    }

    private async Task DeleteExpiredMessagesAsync(CancellationToken cancellationToken)
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Get dialogs with TTL enabled
        var dialogCollection = _database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
        var dialogFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Exists("TtlPeriod"),
            Builders<BsonDocument>.Filter.Gt("TtlPeriod", 0)
        );

        var dialogsWithTTL = await dialogCollection.Find(dialogFilter).ToListAsync(cancellationToken);

        if (dialogsWithTTL.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} dialogs with TTL enabled", dialogsWithTTL.Count);

        var messageCollection = _database.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        int totalDeleted = 0;

        foreach (var dialog in dialogsWithTTL)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var ttlPeriod = dialog["TtlPeriod"].AsInt32;
            var expirationTime = currentTime - ttlPeriod;

            // Extract userId and peerId from dialog ID
            // DialogId format: "userId_peerType_peerId"
            var dialogId = dialog["_id"].AsString;
            var parts = dialogId.Split('_');
            if (parts.Length != 3)
            {
                continue;
            }

            var userId = long.Parse(parts[0]);
            var peerType = int.Parse(parts[1]);
            var peerId = long.Parse(parts[2]);

            // Delete expired messages
            var messageFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", userId),
                Builders<BsonDocument>.Filter.Eq("ToPeerType", peerType),
                Builders<BsonDocument>.Filter.Eq("ToPeerId", peerId),
                Builders<BsonDocument>.Filter.Lt("Date", (int)expirationTime),
                Builders<BsonDocument>.Filter.Ne("Deleted", true)
            );

            var update = Builders<BsonDocument>.Update
                .Set("Deleted", true)
                .Set("DeletedAt", DateTime.UtcNow);

            var result = await messageCollection.UpdateManyAsync(messageFilter, update, cancellationToken: cancellationToken);

            if (result.ModifiedCount > 0)
            {
                totalDeleted += (int)result.ModifiedCount;
                _logger.LogInformation(
                    "Deleted {Count} expired messages for dialog {DialogId} (TTL: {TTL}s)",
                    result.ModifiedCount,
                    dialogId,
                    ttlPeriod
                );
            }
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Total messages auto-deleted: {Count}", totalDeleted);
        }
    }
}
