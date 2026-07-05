using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Push;

/// <summary>
/// Records the fact that a PUSH notification for a given message was delivered to and acknowledged by the
/// client, as reported via <c>messages.reportMessagesDelivery</c> (see
/// <a href="https://corefork.telegram.org/api/push-updates">Handling PUSH-notifications</a>).
/// <para>
/// The receipt is stored as a Redis key per <c>(peerId, messageId)</c> with a TTL equal to the reporting
/// window, providing an idempotent record of delivery that can be used to de-duplicate notifications.
/// If Redis is unavailable the store fails open and reports the receipt as "newly marked" so that the RPC
/// still succeeds — matching the conservative posture of the other Redis-backed push stores.
/// </para>
/// </summary>
public interface IPushDeliveryReceiptStore
{
    /// <summary>
    /// Marks the notification for <paramref name="messageId"/> in <paramref name="peerId"/> as delivered.
    /// Returns <c>true</c> when this is the first time the receipt is recorded (useful for de-duplication),
    /// or <c>false</c> when a receipt already existed within the reporting window.
    /// </summary>
    Task<bool> MarkDeliveredAsync(long peerId, int messageId);
}

public sealed class PushDeliveryReceiptStore(IConnectionMultiplexer redis, ILogger<PushDeliveryReceiptStore> logger)
    : IPushDeliveryReceiptStore, ISingletonDependency
{
    private const string KeyPrefix = "push:delivered:";

    /// <summary>TTL of the delivery-receipt de-duplication window.</summary>
    private static readonly TimeSpan ReceiptTtl = TimeSpan.FromHours(24);

    public async Task<bool> MarkDeliveredAsync(long peerId, int messageId)
    {
        try
        {
            var db = redis.GetDatabase();
            // SET key "1" NX EX <ttl>: returns true only when the key did not already exist.
            return await db.StringSetAsync(Key(peerId, messageId), "1", ReceiptTtl, When.NotExists);
        }
        catch (Exception ex)
        {
            // Fail open: if Redis is unreachable, treat the receipt as newly marked so the report still succeeds.
            logger.LogDebug(ex,
                "Failed to mark delivery receipt for peerId={PeerId}, messageId={MessageId}; assuming first delivery",
                peerId, messageId);
            return true;
        }
    }

    private static RedisKey Key(long peerId, int messageId) => $"{KeyPrefix}{peerId}:{messageId}";
}
