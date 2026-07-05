using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 30: Подтверждение доставки фиксирует все сообщения.
//
// For any messages.reportMessagesDelivery request carrying the Push flag and a set of (peer, msgIds),
// the Сервис_Подтверждения_Доставки records the delivery for every msgId and the RPC succeeds
// (boolTrue). Modelled at the store level: PushDeliveryReceiptStore.MarkDeliveredAsync is invoked for
// each (peerId, msgId) pair. The first time a receipt is recorded the call returns true, and the
// corresponding Redis key (push:delivered:{peerId}:{messageId}) exists afterwards. The store is backed
// by an in-memory fake IConnectionMultiplexer/IDatabase so the property runs without a real Redis
// server (StringSetAsync / KeyExistsAsync over a shared key set).
//
// Validates: Requirements 8.5
public class Property30_DeliveryReceiptMarksAllTests
{
    /// <summary>Mirrors the store's internal key layout so the test can confirm the receipt landed.</summary>
    private const string KeyPrefix = "push:delivered:";

    /// <summary>Positive peer id, reusing the catalogue's identifier range.</summary>
    private static Gen<long> PeerId => PushGen.PositiveId;

    /// <summary>A non-empty set of distinct message ids (the msgIds carried by one report request).</summary>
    private static Gen<int[]> MsgIdSet =>
        from count in Gen.Choose(1, 12)
        from ids in GenHelpers.ArrayOfLength(count, Gen.Choose(1, 100_000))
        select ids.Distinct().ToArray();

    // Property 30: Подтверждение доставки фиксирует все сообщения
    // Validates: Requirements 8.5
    [Property(MaxTest = 100)]
    public Property Delivery_receipt_records_every_message()
    {
        return Prop.ForAll(Arb.From(PeerId), Arb.From(MsgIdSet), (peerId, msgIds) =>
        {
            var redis = FakeRedis.CreateConnectionMultiplexer();
            var store = new PushDeliveryReceiptStore(redis, NullLogger<PushDeliveryReceiptStore>.Instance);
            var db = redis.GetDatabase();

            foreach (var msgId in msgIds)
            {
                // First report for this (peer, msgId): the receipt is newly recorded.
                var firstMark = store.MarkDeliveredAsync(peerId, msgId).GetAwaiter().GetResult();

                // The receipt is now persisted under the per-message key.
                var key = $"{KeyPrefix}{peerId}:{msgId}";
                var recorded = db.KeyExistsAsync(key).GetAwaiter().GetResult();

                if (!firstMark || !recorded)
                {
                    return false
                        .Label($"peerId={peerId}, msgId={msgId}, firstMark={firstMark}, recorded={recorded}");
                }
            }

            return true.ToProperty()
                .Label($"peerId={peerId}, msgIds=[{string.Join(",", msgIds)}]");
        });
    }
}
