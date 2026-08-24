using System.Collections.Concurrent;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// In-process snapshot of the third-party verification badges, so converting a user or a channel
/// does not hit MongoDB.
/// <para>
/// <c>user.bot_verification_icon</c> and <c>channel.bot_verification_icon</c> have to be filled for
/// every peer in every dialog list, search result and message batch, and several of those conversion
/// paths are synchronous - a per-peer query there would be both an N+1 and a blocking call on a
/// thread pool thread.
/// </para>
/// <para>
/// The snapshot is refreshed from the async conversion paths, at most once per
/// <see cref="RefreshInterval"/>, and updated in place when this process issues or revokes a badge.
/// A badge granted by another process is therefore visible in lists after at most one refresh
/// interval; the authoritative reads - <c>users.getFullUser</c>, <c>channels.getFullChannel</c> and
/// <c>messages.checkChatInvite</c> - go straight to the collection and are never stale.
/// </para>
/// </summary>
public interface IBotVerificationCache
{
    /// <summary>The badge icon of a user, or <c>0</c> when the user is not verified.</summary>
    long GetUserIcon(long userId);

    /// <inheritdoc cref="GetUserIcon"/>
    long GetChannelIcon(long channelId);

    /// <summary>Reloads the snapshot when it is older than <see cref="RefreshInterval"/>.</summary>
    Task EnsureFreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a badge this process just issued.</summary>
    void Apply(BotVerificationDocument document);

    /// <summary>Drops a badge this process just revoked.</summary>
    void Remove(long userId, long channelId);
}

/// <inheritdoc />
public class BotVerificationCache(IMongoDatabase mongoDatabase, ILogger<BotVerificationCache> logger)
    : IBotVerificationCache, ISingletonDependency
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, long> _userIcons = new();
    private readonly ConcurrentDictionary<long, long> _channelIcons = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private DateTime _loadedAt = DateTime.MinValue;

    public long GetUserIcon(long userId)
    {
        return userId != 0 && _userIcons.TryGetValue(userId, out var icon) ? icon : 0;
    }

    public long GetChannelIcon(long channelId)
    {
        return channelId != 0 && _channelIcons.TryGetValue(channelId, out var icon) ? icon : 0;
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        if (DateTime.UtcNow - _loadedAt < RefreshInterval)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            // Another request is already reloading; serving the previous snapshot for a moment
            // longer is better than queueing every converter behind one query.
            return;
        }

        try
        {
            if (DateTime.UtcNow - _loadedAt < RefreshInterval)
            {
                return;
            }

            var documents = await mongoDatabase
                .GetCollection<BotVerificationDocument>(BotVerificationStore.CollectionName)
                .Find(Builders<BotVerificationDocument>.Filter.Empty)
                .ToListAsync(cancellationToken);

            var users = new HashSet<long>();
            var channels = new HashSet<long>();

            foreach (var document in documents)
            {
                if (document.UserId != 0)
                {
                    _userIcons[document.UserId] = document.Icon;
                    users.Add(document.UserId);
                }
                else if (document.ChannelId != 0)
                {
                    _channelIcons[document.ChannelId] = document.Icon;
                    channels.Add(document.ChannelId);
                }
            }

            foreach (var userId in _userIcons.Keys.Where(p => !users.Contains(p)))
            {
                _userIcons.TryRemove(userId, out _);
            }

            foreach (var channelId in _channelIcons.Keys.Where(p => !channels.Contains(p)))
            {
                _channelIcons.TryRemove(channelId, out _);
            }

            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // A badge that cannot be loaded must not take the dialog list down with it.
            logger.LogWarning(ex, "Failed to refresh the bot verification cache");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Apply(BotVerificationDocument document)
    {
        if (document.UserId != 0)
        {
            _userIcons[document.UserId] = document.Icon;
        }
        else if (document.ChannelId != 0)
        {
            _channelIcons[document.ChannelId] = document.Icon;
        }
    }

    public void Remove(long userId, long channelId)
    {
        if (userId != 0)
        {
            _userIcons.TryRemove(userId, out _);
        }

        if (channelId != 0)
        {
            _channelIcons.TryRemove(channelId, out _);
        }
    }
}
