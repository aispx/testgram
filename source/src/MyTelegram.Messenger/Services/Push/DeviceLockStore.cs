using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Push;

/// <summary>
/// Tracks the client-side passcode lock state per <c>permAuthKeyId</c>, mirroring the behaviour
/// described in <a href="https://corefork.telegram.org/api/push-updates">Handling PUSH-notifications</a>:
/// when a device reports an inactivity period (<c>account.updateDeviceLocked</c>), message texts must be
/// hidden in incoming PUSH notifications until the lock is cleared.
/// <para>
/// The lock is stored as a Redis key per <c>permAuthKeyId</c> with a TTL equal to the requested period.
/// A period of <c>0</c> clears the lock immediately. If Redis is unavailable, <see cref="IsLockedAsync"/>
/// fails open and reports "not locked" — an extra (un-hidden) push is the safe fallback for availability,
/// matching the conservative posture of <see cref="IPushOnlineFilter"/>.
/// </para>
/// </summary>
public interface IDeviceLockStore
{
    /// <summary>
    /// Sets or clears the device lock for the given auth key. When <paramref name="periodSeconds"/> is
    /// greater than 0 the lock is stored with a TTL equal to the period; when it is 0 the lock is removed.
    /// </summary>
    Task SetAsync(long permAuthKeyId, int periodSeconds);

    /// <summary>True when the device's auth key currently has an active passcode lock (hide message texts).</summary>
    Task<bool> IsLockedAsync(long permAuthKeyId);
}

public sealed class DeviceLockStore(IConnectionMultiplexer redis, ILogger<DeviceLockStore> logger)
    : IDeviceLockStore, ISingletonDependency
{
    private const string KeyPrefix = "push:locked:";

    public async Task SetAsync(long permAuthKeyId, int periodSeconds)
    {
        try
        {
            var db = redis.GetDatabase();
            if (periodSeconds > 0)
            {
                await db.StringSetAsync(Key(permAuthKeyId), "1", TimeSpan.FromSeconds(periodSeconds));
            }
            else
            {
                await db.KeyDeleteAsync(Key(permAuthKeyId));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to set device lock for permAuthKeyId={PermAuthKeyId}, period={Period}", permAuthKeyId, periodSeconds);
        }
    }

    public async Task<bool> IsLockedAsync(long permAuthKeyId)
    {
        try
        {
            var db = redis.GetDatabase();
            return await db.KeyExistsAsync(Key(permAuthKeyId));
        }
        catch (Exception ex)
        {
            // Fail open: if Redis is unreachable, assume not locked (show the message text).
            logger.LogDebug(ex, "Lock-check failed for permAuthKeyId={PermAuthKeyId}; assuming not locked", permAuthKeyId);
            return false;
        }
    }

    private static RedisKey Key(long permAuthKeyId) => KeyPrefix + permAuthKeyId.ToString("x");
}
