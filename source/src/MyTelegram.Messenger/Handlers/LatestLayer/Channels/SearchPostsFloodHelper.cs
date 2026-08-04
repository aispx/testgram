using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Daily quota for the <a href="https://corefork.telegram.org/api/search#posts-tab">global post search</a>:
/// a number of free searches per UTC day, after which each search costs Stars.
/// See https://corefork.telegram.org/method/channels.checkSearchPostsFlood
/// </summary>
internal static class SearchPostsFloodHelper
{
    public const string CollectionName = "search_posts_flood";

    /// <summary>Free global post searches per UTC day.</summary>
    public const int TotalDaily = 100;

    /// <summary>Stars charged for a single search once the free quota is used up.</summary>
    public const long StarsAmount = 100;

    private const int SecondsPerDay = 24 * 60 * 60;

    internal sealed record FloodState(int Remains, int WaitTill)
    {
        public bool QueryIsFree => Remains > 0;
    }

    public static async Task<FloodState> GetStateAsync(IMongoDatabase database, long userId)
    {
        var used = await GetUsedTodayAsync(database, userId);
        return BuildState(used);
    }

    /// <summary>
    /// Consumes one free search. Returns false when today's quota is exhausted, in which case the
    /// caller has to charge Stars instead.
    /// </summary>
    public static async Task<bool> TryConsumeFreeSearchAsync(IMongoDatabase database, long userId)
    {
        var today = GetToday();
        var collection = database.GetCollection<BsonDocument>(CollectionName);

        // Reset the counter when the stored day rolled over, then increment atomically.
        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", userId),
                Builders<BsonDocument>.Filter.Ne("Date", today)),
            Builders<BsonDocument>.Update.Set("Date", today).Set("Used", 0));

        var doc = await collection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update
                .SetOnInsert("Date", today)
                .Inc("Used", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        var used = doc.GetValue("Used", 0).ToInt32();
        if (used <= TotalDaily)
        {
            return true;
        }

        // Over quota: undo the increment so the counter cannot drift upwards on paid searches.
        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", userId),
            Builders<BsonDocument>.Update.Inc("Used", -1));

        return false;
    }

    private static async Task<int> GetUsedTodayAsync(IMongoDatabase database, long userId)
    {
        var doc = await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", userId))
            .FirstOrDefaultAsync();

        if (doc == null || doc.GetValue("Date", 0).ToInt32() != GetToday())
        {
            return 0;
        }

        return doc.GetValue("Used", 0).ToInt32();
    }

    private static FloodState BuildState(int used)
    {
        var remains = Math.Max(0, TotalDaily - used);

        // wait_till only matters once nothing is left: it points at the next daily reset.
        var waitTill = remains > 0 ? 0 : (GetToday() + 1) * SecondsPerDay;
        return new FloodState(remains, waitTill);
    }

    private static int GetToday()
    {
        return (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / SecondsPerDay);
    }
}
