using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Allow a user to send us messages without paying if <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a> are enabled.
/// Possible errors
/// Code Type Description
/// 400 PARENT_PEER_INVALID The specified <code>parent_peer</code> is invalid.
/// 400 UNSUPPORTED <code>require_payment</code> cannot be <em>set</em> by users, only by monoforums: users must instead use the <a href="https://corefork.telegram.org/constructor/inputPrivacyKeyNoPaidMessages">inputPrivacyKeyNoPaidMessages</a> privacy setting to remove a previously added exemption.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.toggleNoPaidMessagesException"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: account.toggleNoPaidMessagesException#fe2eda76 flags:# refund_charged:flags.0?true require_payment:flags.2?true parent_peer:flags.1?InputPeer user_id:InputUser = Bool;
/// </remarks>
internal sealed class ToggleNoPaidMessagesExceptionHandler(
    IMongoDatabase database,
    IMessageAppService messageAppService,
    ILogger<ToggleNoPaidMessagesExceptionHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestToggleNoPaidMessagesException, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestToggleNoPaidMessagesException obj)
    {
        // Extract target user ID
        long targetUserId = 0;
        if (obj.UserId is MyTelegram.Schema.TInputUser inputUser)
        {
            targetUserId = inputUser.UserId;
        }

        if (targetUserId == 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Check if target user exists
        var userCol = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var targetUser = await userCol.Find(
            Builders<BsonDocument>.Filter.Eq("UserId", targetUserId)
        ).FirstOrDefaultAsync();

        if (targetUser == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Extract parent peer (monoforum) if provided
        long? parentPeerId = null;
        if (obj.ParentPeer != null)
        {
            if (obj.ParentPeer is MyTelegram.Schema.TInputPeerChannel inputPeerChannel)
            {
                parentPeerId = inputPeerChannel.ChannelId;
            }
            else if (obj.ParentPeer is MyTelegram.Schema.TInputPeerChannelFromMessage inputPeerChannelFromMessage)
            {
                parentPeerId = inputPeerChannelFromMessage.ChannelId;
            }
        }

        // Validate: require_payment can only be set by monoforums
        if (obj.RequirePayment && parentPeerId == null)
        {
            logger.LogWarning("User {UserId} tried to set require_payment without parent_peer", input.UserId);
            RpcErrors.RpcErrors400.Unsupported.ThrowRpcError();
        }

        // Get paid messages revenue if refund is requested
        long refundAmount = 0;
        if (obj.RefundCharged)
        {
            var revenueCol = database.GetCollection<BsonDocument>("paid_messages_revenue");
            var revenueFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ReceiverUserId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("SenderUserId", targetUserId),
                Builders<BsonDocument>.Filter.Eq("Refunded", false)
            );

            if (parentPeerId.HasValue)
            {
                revenueFilter = Builders<BsonDocument>.Filter.And(
                    revenueFilter,
                    Builders<BsonDocument>.Filter.Eq("ParentPeerId", parentPeerId.Value)
                );
            }

            var revenueRecords = await revenueCol.Find(revenueFilter).ToListAsync();
            refundAmount = revenueRecords.Sum(r => r["StarsAmount"].AsInt64);

            if (refundAmount > 0)
            {
                // Mark as refunded
                var update = Builders<BsonDocument>.Update.Set("Refunded", true);
                await revenueCol.UpdateManyAsync(revenueFilter, update);

                // Refund stars to sender
                var senderUpdate = Builders<BsonDocument>.Update.Inc("StarsBalance", refundAmount);
                await userCol.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("UserId", targetUserId),
                    senderUpdate
                );

                // Deduct stars from receiver
                var receiverUpdate = Builders<BsonDocument>.Update.Inc("StarsBalance", -refundAmount);
                await userCol.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    receiverUpdate
                );

                logger.LogInformation("Refunded {Amount} stars from {ReceiverId} to {SenderId}",
                    refundAmount, input.UserId, targetUserId);

                // Send service message about refund
                var refundAction = new MyTelegram.Schema.TMessageActionPaidMessagesRefunded
                {
                    Count = revenueRecords.Count,
                    Stars = refundAmount
                };

                var sendInput = new SendMessageInput(
                    input.ToRequestInfo() with { ReqMsgId = 0 },
                    input.UserId,
                    new Peer(PeerType.User, targetUserId),
                    string.Empty,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: refundAction
                );

                await messageAppService.SendMessageAsync([sendInput]);
            }
        }

        // Update exception list
        var exceptionsCol = database.GetCollection<BsonDocument>("paid_messages_exceptions");
        var exceptionId = parentPeerId.HasValue
            ? $"exception-{input.UserId}-{parentPeerId.Value}-{targetUserId}"
            : $"exception-{input.UserId}-{targetUserId}";

        if (obj.RequirePayment)
        {
            // Remove exception (require payment)
            await exceptionsCol.DeleteOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", exceptionId)
            );

            logger.LogInformation("Removed paid messages exception: {ExceptionId}", exceptionId);
        }
        else
        {
            // Add exception (allow without payment)
            var exceptionDoc = new BsonDocument
            {
                ["_id"] = exceptionId,
                ["OwnerUserId"] = input.UserId,
                ["TargetUserId"] = targetUserId,
                ["CreatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (parentPeerId.HasValue)
            {
                exceptionDoc["ParentPeerId"] = parentPeerId.Value;
            }

            await exceptionsCol.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", exceptionId),
                exceptionDoc,
                new ReplaceOptions { IsUpsert = true }
            );

            logger.LogInformation("Added paid messages exception: {ExceptionId}", exceptionId);
        }

        // Update stars rating if refund occurred
        if (refundAmount > 0)
        {
            // Decrease receiver's rating
            var receiverRatingUpdate = Builders<BsonDocument>.Update.Inc("StarsRating", -refundAmount);
            await userCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                receiverRatingUpdate
            );

            logger.LogInformation("Decreased stars rating for {UserId} by {Amount}",
                input.UserId, refundAmount);
        }

        // Update monoForumDialog to trigger updateMonoForumNoPaidException
        if (parentPeerId.HasValue)
        {
            // Update the nopaid_messages_exception flag in the monoforum dialog
            // This will trigger updateMonoForumNoPaidException to be sent to admins
            var dialogCol = database.GetCollection<BsonDocument>("eventflow-dialogreadmodel");
            var dialogFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", parentPeerId.Value),
                Builders<BsonDocument>.Filter.Eq("ToPeerId", targetUserId)
            );

            var dialogUpdate = Builders<BsonDocument>.Update.Set("NoPaidMessagesException", !obj.RequirePayment);
            await dialogCol.UpdateOneAsync(dialogFilter, dialogUpdate);

            logger.LogInformation("MonoForum exception updated: ChannelId={ChannelId}, TargetUserId={TargetUserId}, Exception={Exception}",
                parentPeerId.Value, targetUserId, !obj.RequirePayment);

            // The updateMonoForumNoPaidException will be automatically sent to:
            // - Other monoforum admins
            // - Other sessions of the current admin
            // This happens through the event sourcing system when the dialog is updated
        }

        return new TBoolTrue();
    }
}