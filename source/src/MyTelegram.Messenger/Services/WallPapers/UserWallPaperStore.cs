using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.WallPapers;

/// <inheritdoc />
public sealed class UserWallPaperStore(IMongoDatabase database, IWallPaperCatalog catalog)
    : IUserWallPaperStore, ITransientDependency
{
    private const string CollectionName = "user_wallpapers";

    public async Task<List<MyTelegram.Schema.IWallPaper>> GetListAsync(long userId)
    {
        var rows = await Collection.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId)).ToListAsync();

        var settingsById = new Dictionary<long, MyTelegram.Schema.IWallPaperSettings?>();
        var savedIds = new List<long>();
        var known = new HashSet<long>();

        foreach (var row in rows.OrderByDescending(p => p.GetValue("Order", 0L).ToInt64()))
        {
            var wallPaperId = row.GetValue("WallpaperId", 0L).ToInt64();
            if (wallPaperId == 0 || !known.Add(wallPaperId))
            {
                continue;
            }

            if (row.GetValue("Removed", false).ToBoolean())
            {
                continue;
            }

            savedIds.Add(wallPaperId);
            settingsById[wallPaperId] = WallPaperSettingsSerializer.FromBson(row.GetValue("Settings", BsonNull.Value));
        }

        var saved = await catalog.FindManyAsync(savedIds, []);
        var savedById = saved.ToDictionary(p => p.WallPaperId);

        var ordered = new List<WallPaperRow>();
        foreach (var wallPaperId in savedIds)
        {
            if (savedById.TryGetValue(wallPaperId, out var row))
            {
                ordered.Add(row);
            }
        }

        // A listed wallpaper the user has neither saved nor removed still belongs to the list.
        ordered.AddRange((await catalog.GetListedAsync()).Where(p => !known.Contains(p.WallPaperId)));

        var result = new List<MyTelegram.Schema.IWallPaper>(ordered.Count);
        foreach (var row in ordered)
        {
            var settings = settingsById.GetValueOrDefault(row.WallPaperId) ?? row.Settings;
            var wallPaper = await catalog.BuildAsync(row, userId, settings);
            if (wallPaper != null)
            {
                result.Add(wallPaper);
            }
        }

        return result;
    }

    public async Task SaveAsync(long userId, WallPaperRow row, MyTelegram.Schema.IWallPaperSettings? settings)
    {
        var document = new BsonDocument
        {
            { "_id", Key(userId, row.WallPaperId) },
            { "UserId", userId },
            { "WallpaperId", row.WallPaperId },
            { "Removed", false },
            { "Order", await NextOrderAsync(userId) },
            { "Date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            { "Settings", WallPaperSettingsSerializer.ToBson(settings) }
        };

        await Collection.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", Key(userId, row.WallPaperId)),
            document, new ReplaceOptions { IsUpsert = true });
    }

    public async Task UnsaveAsync(long userId, WallPaperRow row)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", Key(userId, row.WallPaperId));

        if (!row.Listed)
        {
            await Collection.DeleteOneAsync(filter);

            return;
        }

        await Collection.ReplaceOneAsync(filter, new BsonDocument
        {
            { "_id", Key(userId, row.WallPaperId) },
            { "UserId", userId },
            { "WallpaperId", row.WallPaperId },
            { "Removed", true },
            { "Order", 0L },
            { "Date", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        }, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<bool> IsSavedAsync(long userId, long wallPaperId)
    {
        var doc = await Collection.Find(Builders<BsonDocument>.Filter.Eq("_id", Key(userId, wallPaperId)))
            .FirstOrDefaultAsync();

        return doc != null && !doc.GetValue("Removed", false).ToBoolean();
    }

    public Task ResetAsync(long userId)
    {
        return Collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId));
    }

    private IMongoCollection<BsonDocument> Collection => database.GetCollection<BsonDocument>(CollectionName);

    private static string Key(long userId, long wallPaperId)
    {
        return $"{userId}:{wallPaperId}";
    }

    /// <summary>
    /// A per-user counter, so re-saving a wallpaper moves it to the front without rewriting anyone
    /// else's rows. Same shape as the saved-GIF order counter.
    /// </summary>
    private async Task<long> NextOrderAsync(long userId)
    {
        var result = await database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"saved_wallpapers_order_{userId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

        return result["seq"].ToInt64();
    }
}
