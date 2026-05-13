using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Handles refund of a suggested-post settlement when the published post is deleted before
/// <c>stars_suggested_post_age_min</c> seconds have elapsed.
/// Looks up <c>suggested_post_settlements</c> by (monoforumChannelId, originalMsgId), reverses
/// the balance movement, marks the settlement as refunded and emits
/// <see cref="TMessageActionSuggestedPostRefund"/> as a service message into the monoforum.
/// </summary>
public static class SuggestedPostRefundHelper
{
    public const string SettlementsCollection = "suggested_post_settlements";

    /// <summary>
    /// Try to refund all pending settlements whose <c>BroadcastChannelId == broadcastChannelId</c>
    /// and <c>OriginalMsgId</c> ∈ <paramref name="msgIds"/>.
    /// Returns the list of refunded settlement docs (may be empty).
    /// </summary>
    public static async Task<List<SuggestedPostSettlementDocument>> TryRefundOnDeleteAsync(
        IMongoDatabase db,
        IMessageAppService messageAppService,
        long broadcastChannelId,
        IReadOnlyCollection<int> msgIds,
        long actorUserId,
        bool payerInitiated = false,
        IObjectMessageSender? objectMessageSender = null,
        long? channelOwnerUserId = null)
    {
        if (msgIds.Count == 0) return [];

        var col = db.GetCollection<SuggestedPostSettlementDocument>(SettlementsCollection);
        // The published post lives inside the broadcast channel; track via BroadcastChannelId.
        var filter = Builders<SuggestedPostSettlementDocument>.Filter.Eq(x => x.Status, "pending")
                     & Builders<SuggestedPostSettlementDocument>.Filter.Eq(x => x.BroadcastChannelId, broadcastChannelId)
                     & Builders<SuggestedPostSettlementDocument>.Filter.In(x => x.OriginalMsgId, msgIds);

        var matches = await col.Find(filter).ToListAsync();
        if (matches.Count == 0) return [];

        var refunded = new List<SuggestedPostSettlementDocument>();
        foreach (var doc in matches)
        {
            try
            {
                if (doc.Currency == ChannelRevenueHelper.TonCurrency)
                {
                    await ChannelRevenueHelper.DebitAsync(db, doc.BroadcastChannelId,
                        ChannelRevenueHelper.TonCurrency, doc.Amount,
                        targetUserId: doc.AuthorUserId, refund: true,
                        title: "Suggested post refund (TON)");
                    await TonBalanceHelper.AddBalanceAsync(db, doc.AuthorUserId, doc.Amount);
                    await TonBalanceHelper.AddTransactionAsync(db, doc.AuthorUserId, doc.Amount,
                        refund: true,
                        peerChannelId: doc.BroadcastChannelId,
                        title: "Suggested post refund (TON)");
                }
                else
                {
                    await ChannelRevenueHelper.DebitAsync(db, doc.BroadcastChannelId,
                        ChannelRevenueHelper.StarsCurrency, doc.Amount,
                        targetUserId: doc.AuthorUserId, refund: true,
                        title: "Suggested post refund");
                    await StarsBalanceHelper.AddBalanceAsync(db, doc.AuthorUserId, doc.Amount);
                    await StarsBalanceHelper.AddTransactionAsync(db, doc.AuthorUserId, doc.Amount,
                        peerChannelId: doc.BroadcastChannelId,
                        title: "Suggested post refund");
                }

                await col.UpdateOneAsync(
                    Builders<SuggestedPostSettlementDocument>.Filter.Eq(x => x.Id, doc.Id),
                    Builders<SuggestedPostSettlementDocument>.Update.Set(x => x.Status, "refunded"));

                // Emit messageActionSuggestedPostRefund into the monoforum, replying to the original suggestion.
                var refundAction = new TMessageActionSuggestedPostRefund { PayerInitiated = payerInitiated };
                var sendInput = new SendMessageInput(
                    new RequestInfo(
                        ConnectionId: string.Empty,
                        SessionId: 0,
                        ReqMsgId: 0,
                        UserId: actorUserId,
                        AccessHashKeyId: 0,
                        AuthKeyId: 0,
                        PermAuthKeyId: 0,
                        RequestId: Guid.NewGuid(),
                        Layer: 222,
                        Date: DateTime.UtcNow.ToTimestamp(),
                        DeviceType: DeviceType.Android),
                    senderUserId: actorUserId,
                    toPeer: new Peer(PeerType.Channel, doc.MonoforumChannelId),
                    message: string.Empty,
                    randomId: Random.Shared.NextInt64(),
                    inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = doc.OriginalMsgId },
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: refundAction,
                    savedPeerId: new Peer(PeerType.User, doc.AuthorUserId));
                await messageAppService.SendMessageAsync([sendInput]);

                if (objectMessageSender != null)
                {
                    if (doc.Currency == ChannelRevenueHelper.TonCurrency)
                        await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, db, doc.AuthorUserId);
                    else
                        await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, db, doc.AuthorUserId);
                    if (channelOwnerUserId.HasValue)
                    {
                        await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, db,
                            doc.BroadcastChannelId, PeerType.Channel, channelOwnerUserId.Value, doc.Currency);
                    }
                }

                refunded.Add(doc);
            }
            catch
            {
                // Best-effort: skip docs that fail (e.g. channel ledger insufficient — shouldn't happen normally).
            }
        }

        return refunded;
    }
}
