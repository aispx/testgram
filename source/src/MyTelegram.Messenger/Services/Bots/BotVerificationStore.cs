using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// A bot that Telegram (here: the server operator) has authorised to hand out third-party
/// verification badges. There is no API that creates these - the official server grants the status
/// out of band, and so does this one: the document is seeded straight into
/// <c>bot-verifier-settings</c>. See https://corefork.telegram.org/api/bots/verification
/// </summary>
[BsonIgnoreExtraElements]
public class BotVerifierSettingsDocument
{
    public long BotId { get; set; }

    /// <summary>Document id of the custom emoji drawn as the badge.</summary>
    public long Icon { get; set; }

    /// <summary>Name of the organisation the badge is issued on behalf of.</summary>
    public string Company { get; set; } = string.Empty;

    /// <summary>Default description, used when the bot does not send one of its own.</summary>
    public string? CustomDescription { get; set; }

    /// <summary>Whether the bot may pick a different description per verified peer.</summary>
    public bool CanModifyCustomDescription { get; set; }
}

/// <summary>
/// Reads and writes the two collections behind
/// <a href="https://corefork.telegram.org/api/bots/verification">third-party verification</a>:
/// <c>bot-verifier-settings</c> (who may verify) and <c>bot-verifications</c> (who is verified).
/// </summary>
public interface IBotVerificationStore
{
    Task<BotVerifierSettingsDocument?> GetVerifierSettingsAsync(long botId,
        CancellationToken cancellationToken = default);

    Task<BotVerificationDocument?> GetForUserAsync(long userId, CancellationToken cancellationToken = default);

    Task<BotVerificationDocument?> GetForChannelAsync(long channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The badges of a whole batch of users in one round trip. Converting a dialog list one
    /// <c>Find</c> per user is what this replaces.
    /// </summary>
    Task<Dictionary<long, BotVerificationDocument>> GetForUsersAsync(IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="GetForUsersAsync"/>
    Task<Dictionary<long, BotVerificationDocument>> GetForChannelsAsync(IReadOnlyCollection<long> channelIds,
        CancellationToken cancellationToken = default);

    Task SetAsync(BotVerificationDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the badge of a peer, but only when <paramref name="botId"/> is the bot that issued it -
    /// one verifier must not be able to revoke another organisation's badge.
    /// Returns whether a document was actually removed.
    /// </summary>
    Task<bool> RemoveAsync(long botId, long userId, long channelId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class BotVerificationStore(IMongoDatabase mongoDatabase, IBotVerificationCache cache)
    : IBotVerificationStore, ITransientDependency
{
    public const string CollectionName = "bot-verifications";
    public const string VerifierSettingsCollectionName = "bot-verifier-settings";

    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<BotVerificationDocument> Collection =>
        mongoDatabase.GetCollection<BotVerificationDocument>(CollectionName);

    private IMongoCollection<BotVerifierSettingsDocument> VerifierSettings =>
        mongoDatabase.GetCollection<BotVerifierSettingsDocument>(VerifierSettingsCollectionName);

    public async Task<BotVerifierSettingsDocument?> GetVerifierSettingsAsync(long botId,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        return await VerifierSettings
            .Find(Builders<BotVerifierSettingsDocument>.Filter.Eq(p => p.BotId, botId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<BotVerificationDocument?> GetForUserAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == 0)
        {
            return null;
        }

        await EnsureIndexesAsync();

        return await Collection.Find(Builders<BotVerificationDocument>.Filter.Eq(p => p.UserId, userId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<BotVerificationDocument?> GetForChannelAsync(long channelId,
        CancellationToken cancellationToken = default)
    {
        if (channelId == 0)
        {
            return null;
        }

        await EnsureIndexesAsync();

        return await Collection.Find(Builders<BotVerificationDocument>.Filter.Eq(p => p.ChannelId, channelId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<Dictionary<long, BotVerificationDocument>> GetForUsersAsync(IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default)
    {
        return GetManyAsync(userIds, forUsers: true, cancellationToken);
    }

    public Task<Dictionary<long, BotVerificationDocument>> GetForChannelsAsync(IReadOnlyCollection<long> channelIds,
        CancellationToken cancellationToken = default)
    {
        return GetManyAsync(channelIds, forUsers: false, cancellationToken);
    }

    public async Task SetAsync(BotVerificationDocument document, CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var filter = document.UserId != 0
            ? Builders<BotVerificationDocument>.Filter.Eq(p => p.UserId, document.UserId)
            : Builders<BotVerificationDocument>.Filter.Eq(p => p.ChannelId, document.ChannelId);

        await Collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true },
            cancellationToken);

        cache.Apply(document);
    }

    public async Task<bool> RemoveAsync(long botId, long userId, long channelId,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexesAsync();

        var peerFilter = userId != 0
            ? Builders<BotVerificationDocument>.Filter.Eq(p => p.UserId, userId)
            : Builders<BotVerificationDocument>.Filter.Eq(p => p.ChannelId, channelId);

        var result = await Collection.DeleteOneAsync(
            peerFilter & Builders<BotVerificationDocument>.Filter.Eq(p => p.BotId, botId), cancellationToken);

        if (result.DeletedCount == 0)
        {
            return false;
        }

        cache.Remove(userId, channelId);

        return true;
    }

    private async Task<Dictionary<long, BotVerificationDocument>> GetManyAsync(IReadOnlyCollection<long> ids,
        bool forUsers,
        CancellationToken cancellationToken)
    {
        var distinctIds = ids.Where(p => p != 0).Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return [];
        }

        await EnsureIndexesAsync();

        var filter = forUsers
            ? Builders<BotVerificationDocument>.Filter.In(p => p.UserId, distinctIds)
            : Builders<BotVerificationDocument>.Filter.In(p => p.ChannelId, distinctIds);

        var documents = await Collection.Find(filter).ToListAsync(cancellationToken);

        var result = new Dictionary<long, BotVerificationDocument>(documents.Count);
        foreach (var document in documents)
        {
            result[forUsers ? document.UserId : document.ChannelId] = document;
        }

        return result;
    }

    /// <summary>Creates the indexes once; a failed attempt is not cached, so the next call retries.</summary>
    private Task EnsureIndexesAsync()
    {
        var pending = Volatile.Read(ref _indexInit);
        if (pending is { IsCompletedSuccessfully: true })
        {
            return pending;
        }

        lock (IndexInitLock)
        {
            if (_indexInit is null || _indexInit.IsFaulted || _indexInit.IsCanceled)
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private async Task CreateIndexesAsync()
    {
        var keys = Builders<BotVerificationDocument>.IndexKeys;

        // Every converted user and channel looks its badge up here, so an unindexed collection means
        // a collection scan per peer in every dialog list.
        await Collection.Indexes.CreateManyAsync([
            new CreateIndexModel<BotVerificationDocument>(keys.Ascending(p => p.UserId),
                new CreateIndexOptions { Name = "bot_verifications_user" }),
            new CreateIndexModel<BotVerificationDocument>(keys.Ascending(p => p.ChannelId),
                new CreateIndexOptions { Name = "bot_verifications_channel" }),
            new CreateIndexModel<BotVerificationDocument>(keys.Ascending(p => p.BotId),
                new CreateIndexOptions { Name = "bot_verifications_bot" })
        ]);

        await VerifierSettings.Indexes.CreateOneAsync(
            new CreateIndexModel<BotVerifierSettingsDocument>(
                Builders<BotVerifierSettingsDocument>.IndexKeys.Ascending(p => p.BotId),
                new CreateIndexOptions { Name = "bot_verifier_settings_bot", Unique = true }));
    }
}
