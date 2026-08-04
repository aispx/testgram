using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <inheritdoc cref="IChannelEmojiStatusValidator"/>
public class ChannelEmojiStatusValidator(IMongoDatabase mongoDatabase)
    : IChannelEmojiStatusValidator, ITransientDependency
{
    private readonly IMongoCollection<BsonDocument> _stickerSetCollection =
        mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");

    private readonly IMongoCollection<BsonDocument> _restrictedCollection =
        mongoDatabase.GetCollection<BsonDocument>("channel_restricted_status_emojis");

    public async Task<List<long>> GetAllowedDocumentIdsAsync()
    {
        var sets = await _stickerSetCollection
            .Find(Builders<BsonDocument>.Filter.Eq("ChannelEmojiStatus", true))
            .Project(Builders<BsonDocument>.Projection.Include("DocumentIds"))
            .ToListAsync();

        var restricted = (await GetRestrictedDocumentIdsAsync()).ToHashSet();

        return sets
            .Where(p => p.TryGetValue("DocumentIds", out var ids) && ids.IsBsonArray)
            .SelectMany(p => p["DocumentIds"].AsBsonArray.Select(id => id.ToInt64()))
            .Where(id => id != 0 && !restricted.Contains(id))
            .Distinct()
            .ToList();
    }

    public async Task<List<long>> GetRestrictedDocumentIdsAsync()
    {
        var docs = await _restrictedCollection
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Project(Builders<BsonDocument>.Projection.Include("DocumentId"))
            .ToListAsync();

        return docs
            .Where(p => p.TryGetValue("DocumentId", out var value) && !value.IsBsonNull)
            .Select(p => p["DocumentId"].ToInt64())
            .Where(id => id != 0)
            .Distinct()
            .ToList();
    }

    public async Task<bool> IsAllowedAsync(long documentId)
    {
        if ((await GetRestrictedDocumentIdsAsync()).Contains(documentId))
        {
            return false;
        }

        var hasChannelStatusSet = await _stickerSetCollection
            .Find(Builders<BsonDocument>.Filter.Eq("ChannelEmojiStatus", true))
            .Limit(1)
            .AnyAsync();

        // Without a curated channel status pack there is nothing to validate against, so allow any
        // custom emoji rather than making the method unusable on this server.
        if (!hasChannelStatusSet)
        {
            return true;
        }

        return await _stickerSetCollection
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelEmojiStatus", true),
                Builders<BsonDocument>.Filter.AnyEq("DocumentIds", new BsonInt64(documentId))))
            .Limit(1)
            .AnyAsync();
    }
}
