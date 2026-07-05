using StackExchange.Redis;
using MyTelegram.Messenger.QueryServer.EventHandlers;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Decides whether a push notification should actually be delivered to a device, mirroring the
/// behaviour of upstream Telegram: when a user already has an active MTProto connection, the
/// message/update reaches them directly over that connection and a battery-heavy FCM/APNS push
/// is suppressed.
/// <para>
/// Online state is tracked as a Redis key per <c>permAuthKeyId</c> with a short TTL, refreshed by
/// <see cref="PushSessionActivityHandler"/> from incoming traffic (RPC requests, heartbeat-like
/// pings). If the Redis-backed tracker is unavailable the filter falls back to "always send",
/// which is the safe choice for correctness (an extra push is harmless; a dropped push is not).
/// </para>
/// </summary>
public interface IPushOnlineFilter
{
    /// <summary>True when the device's auth key currently has an active MTProto session (push should be skipped).</summary>
    Task<bool> IsOnlineAsync(long permAuthKeyId);

    /// <summary>Marks an auth key as currently connected, refreshing the TTL.</summary>
    Task MarkOnlineAsync(long permAuthKeyId);
}

public class PushOnlineFilter(IConnectionMultiplexer redis, ILogger<PushOnlineFilter> logger)
    : IPushOnlineFilter, ISingletonDependency
{
    // The TTL must be longer than the typical client poll interval so a healthy session stays
    // "online" between requests, but short enough to expire soon after the socket closes.
    private const int OnlineTtlSeconds = 90;
    private const string KeyPrefix = "push:online:";

    public async Task<bool> IsOnlineAsync(long permAuthKeyId)
    {
        try
        {
            var db = redis.GetDatabase();
            return await db.KeyExistsAsync(Key(permAuthKeyId));
        }
        catch (Exception ex)
        {
            // Redis unreachable: be conservative and assume offline (send the push).
            logger.LogDebug(ex, "Online-check failed for permAuthKeyId={PermAuthKeyId}; assuming offline", permAuthKeyId);
            return false;
        }
    }

    public async Task MarkOnlineAsync(long permAuthKeyId)
    {
        try
        {
            var db = redis.GetDatabase();
            await db.StringSetAsync(Key(permAuthKeyId), "1", TimeSpan.FromSeconds(OnlineTtlSeconds));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to mark online permAuthKeyId={PermAuthKeyId}", permAuthKeyId);
        }
    }

    private static string Key(long permAuthKeyId) => KeyPrefix + permAuthKeyId.ToString("x");
}
