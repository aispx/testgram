using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;
using StackExchange.Redis;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 30: The delivery receipt records every message.
//
// For any messages.reportMessagesDelivery request carrying the Push flag and a set of
// (peerId, msgId) pairs, the Delivery_Receipt_Service records a delivery receipt for
// every msgId and the RPC succeeds (boolTrue). This exercises
// PushDeliveryReceiptStore.MarkDeliveredAsync over an in-memory fake
// IConnectionMultiplexer/IDatabase (StringSet NX with TTL, KeyExists), so the property runs
// without a real Redis server. After marking, every (peerId, msgId) key is present in the
// store, and the first MarkDeliveredAsync for each distinct pair reports a newly recorded
// receipt — modelling the boolTrue return of the handler.
//
// Validates: Requirements 8.5
public class Property30_DeliveryReceiptTests
{
    /// <summary>Redis key layout used by <see cref="PushDeliveryReceiptStore"/>.</summary>
    private static RedisKey ReceiptKey(long peerId, int messageId) => $"push:delivered:{peerId}:{messageId}";

    /// <summary>A single reported message: a positive peer id and a positive message id.</summary>
    private static Gen<(long PeerId, int MsgId)> ReportedMessage =>
        from peerId in PushGen.PositiveId
        from msgId in Gen.Choose(1, 1_000_000)
        select (peerId, msgId);

    /// <summary>A non-empty set of distinct (peerId, msgId) pairs, as reported in one request.</summary>
    private static Gen<(long PeerId, int MsgId)[]> ReportedMessageSet =>
        Gen.NonEmptyListOf(ReportedMessage)
            .Select(list => list.Distinct().ToArray());

    // Property 30: The delivery receipt records every message
    // Validates: Requirements 8.5
    [Property(MaxTest = 100)]
    public Property Reporting_delivery_records_every_message()
    {
        return Prop.ForAll(Arb.From(ReportedMessageSet), pairs =>
        {
            var redis = FakeRedis.CreateConnectionMultiplexer();
            var store = new PushDeliveryReceiptStore(redis, NullLogger<PushDeliveryReceiptStore>.Instance);

            // Mark every reported (peerId, msgId) as delivered, capturing the "newly marked" result.
            var allNewlyMarked = true;
            foreach (var (peerId, msgId) in pairs)
            {
                var newlyMarked = store.MarkDeliveredAsync(peerId, msgId).GetAwaiter().GetResult();
                allNewlyMarked &= newlyMarked;
            }

            // Every reported message now has a persisted delivery receipt.
            var db = redis.GetDatabase();
            var allRecorded = pairs.All(p =>
                db.KeyExistsAsync(ReceiptKey(p.PeerId, p.MsgId)).GetAwaiter().GetResult());

            return (allRecorded && allNewlyMarked)
                .Label($"count={pairs.Length}, allRecorded={allRecorded}, allNewlyMarked={allNewlyMarked}");
        });
    }
}
