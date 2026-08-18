using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.AdminLog;

/// <summary>
/// The MongoDB collection backing the <a href="https://corefork.telegram.org/api/recent-actions">admin log</a>
/// and the indexes <c>channels.getAdminLog</c> relies on.
/// </summary>
public static class AdminLogCollection
{
    public const string Name = "channel_admin_log";

    /// <summary>
    /// Creates the query indexes and the retention (TTL) index. Index creation is idempotent, so this may
    /// be called by every process that touches the collection.
    /// </summary>
    public static async Task EnsureIndexesAsync(
        IMongoDatabase database,
        int retentionSeconds,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var collection = database.GetCollection<BsonDocument>(Name);
        var keys = Builders<BsonDocument>.IndexKeys;

        // Plain pagination: newest events of a channel first.
        var byEvent = new CreateIndexModel<BsonDocument>(
            keys.Ascending("channel_id").Descending("event_id"),
            new CreateIndexOptions { Name = "admin_log_channel_event" });

        // events_filter queries: filters is an array, so this is a multikey index.
        var byFilter = new CreateIndexModel<BsonDocument>(
            keys.Ascending("channel_id").Ascending("filters").Descending("event_id"),
            new CreateIndexOptions { Name = "admin_log_channel_filter_event" });

        // admins queries.
        var byAdmin = new CreateIndexModel<BsonDocument>(
            keys.Ascending("channel_id").Ascending("user_id").Descending("event_id"),
            new CreateIndexOptions { Name = "admin_log_channel_user_event" });

        await collection.Indexes.CreateManyAsync([byEvent, byFilter, byAdmin], cancellationToken);

        await EnsureRetentionIndexAsync(database, collection, retentionSeconds, logger, cancellationToken);
    }

    private static async Task EnsureRetentionIndexAsync(
        IMongoDatabase database,
        IMongoCollection<BsonDocument> collection,
        int retentionSeconds,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var ttlIndex = new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("date"),
            new CreateIndexOptions
            {
                Name = TtlIndexName,
                ExpireAfter = TimeSpan.FromSeconds(retentionSeconds)
            });

        try
        {
            await collection.Indexes.CreateOneAsync(ttlIndex, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException e) when (e.CodeName is "IndexOptionsConflict" or "IndexKeySpecsConflict")
        {
            // The index already exists with a different retention: change it in place rather than
            // leaving the previous value silently in effect.
            var command = new BsonDocument
            {
                ["collMod"] = Name,
                ["index"] = new BsonDocument
                {
                    ["name"] = TtlIndexName,
                    ["expireAfterSeconds"] = retentionSeconds
                }
            };

            await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
            logger?.LogInformation("Admin log retention updated to {RetentionSeconds}s", retentionSeconds);
        }
    }

    private const string TtlIndexName = "admin_log_retention";
}
