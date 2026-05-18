using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Approve or reject a <a href="https://corefork.telegram.org/api/suggested-posts">suggested post »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleSuggestedPostApproval"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleSuggestedPostApprovalHandler(IAccessHashHelper accessHashHelper, IPeerHelper peerHelper, IQueryProcessor queryProcessor, IMessageAppService messageAppService, IChannelAppService channelAppService, IMongoDatabase mongoDatabase, IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleSuggestedPostApproval, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestToggleSuggestedPostApproval obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);

        var monoforumPeer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (monoforumPeer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
        if (monoforum == null || !monoforum.IsMonoforum)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var suggestionMessage = (await queryProcessor.ProcessAsync(new GetMessagesQuery(
            monoforumPeer.PeerId,
            MessageType.Unknown,
            null,
            [obj.MsgId],
            0,
            1,
            null,
            null,
            input.UserId,
            0
        ))).FirstOrDefault();

        if (suggestionMessage == null || suggestionMessage.SuggestedPost == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var topicPeer = suggestionMessage.SavedPeerId ?? new Peer(PeerType.User, input.UserId);
        if (topicPeer.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (topicPeer.PeerId != input.UserId)
        {
            var linkedChannelId = monoforum.LinkedMonoforumId;
            var linkedChannel = linkedChannelId.HasValue ? await channelAppService.GetAsync(linkedChannelId.Value) : null;
            var admin = linkedChannel?.AdminList.FirstOrDefault(p => p.UserId == input.UserId);
            var canManageDirect = linkedChannel?.CreatorId == input.UserId || admin?.AdminRights.ManageDirectMessages == true;
            if (!canManageDirect)
            {
                RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
            }
        }

        var balanceTooLow = false;
        // Suggested-post settlement is recorded against the BROADCAST channel (the public side
        // of the monoforum), not the monoforum sat-channel or the creator's user account.
        var revenueChannelId = monoforum.LinkedMonoforumId ?? monoforumPeer.PeerId;

        string? settledCurrency = null;
        long settledAmount = 0;

        if (!obj.Reject && suggestionMessage.SuggestedPost is TSuggestedPost suggested)
        {
            switch (suggested.Price)
            {
                case TStarsAmount starsPrice when starsPrice.Amount > 0:
                    {
                        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, topicPeer.PeerId);
                        if (balance < starsPrice.Amount)
                        {
                            balanceTooLow = true;
                        }
                        else
                        {
                            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, topicPeer.PeerId, -starsPrice.Amount);
                            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, topicPeer.PeerId, -starsPrice.Amount,
                                peerChannelId: revenueChannelId,
                                title: "Suggested post payment");
                            await ChannelRevenueHelper.CreditAsync(mongoDatabase, revenueChannelId,
                                ChannelRevenueHelper.StarsCurrency, starsPrice.Amount,
                                sourceUserId: topicPeer.PeerId,
                                title: "Suggested post revenue");
                            settledCurrency = ChannelRevenueHelper.StarsCurrency;
                            settledAmount = starsPrice.Amount;
                        }
                        break;
                    }
                case TStarsTonAmount tonPrice when tonPrice.Amount > 0:
                    {
                        var balance = await TonBalanceHelper.GetBalanceAsync(mongoDatabase, topicPeer.PeerId);
                        if (balance < tonPrice.Amount)
                        {
                            balanceTooLow = true;
                        }
                        else
                        {
                            await TonBalanceHelper.AddBalanceAsync(mongoDatabase, topicPeer.PeerId, -tonPrice.Amount);
                            await TonBalanceHelper.AddTransactionAsync(mongoDatabase, topicPeer.PeerId, -tonPrice.Amount,
                                peerChannelId: revenueChannelId,
                                title: "Suggested post payment (TON)");
                            await ChannelRevenueHelper.CreditAsync(mongoDatabase, revenueChannelId,
                                ChannelRevenueHelper.TonCurrency, tonPrice.Amount,
                                sourceUserId: topicPeer.PeerId,
                                title: "Suggested post revenue (TON)");
                            settledCurrency = ChannelRevenueHelper.TonCurrency;
                            settledAmount = tonPrice.Amount;
                        }
                        break;
                    }
            }
        }

        // Persist a settlement record so that the background worker can emit
        // messageActionSuggestedPostSuccess after stars_suggested_post_age_min seconds.
        if (settledCurrency != null)
        {
            const int defaultAgeMin = 86400; // matches appConfig stars_suggested_post_age_min
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await mongoDatabase.GetCollection<SuggestedPostSettlementDocument>("suggested_post_settlements")
                .InsertOneAsync(new SuggestedPostSettlementDocument
                {
                    Id = $"sps-{monoforumPeer.PeerId}-{obj.MsgId}",
                    MonoforumChannelId = monoforumPeer.PeerId,
                    BroadcastChannelId = revenueChannelId,
                    OriginalMsgId = obj.MsgId,
                    AuthorUserId = topicPeer.PeerId,
                    ApproverUserId = input.UserId,
                    Currency = settledCurrency,
                    Amount = settledAmount,
                    ApprovedAt = now,
                    SettleAt = now + defaultAgeMin,
                    Status = "pending",
                });

            // Push balance updates so author + channel admin see the deduction immediately.
            if (settledCurrency == ChannelRevenueHelper.TonCurrency)
                await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, topicPeer.PeerId);
            else
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, topicPeer.PeerId);
            await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase,
                revenueChannelId, PeerType.Channel, monoforum.CreatorId, settledCurrency);
        }

        var sendInput = new SendMessageInput(
            input.ToRequestInfo(),
            input.UserId,
            monoforumPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = obj.MsgId },
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: MonoforumCompatibilityHelper.CreateApprovalAction(
                suggestionMessage.SuggestedPost,
                obj.Reject,
                balanceTooLow,
                obj.Reject ? obj.RejectComment : null,
                obj.ScheduleDate),
            savedPeerId: topicPeer
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Item 2: flip the original suggestion message's SuggestedPost.Accepted /
        // Rejected flags so clients stop rendering the Accept/Reject inline buttons
        // (clients hide them once either flag is set per the suggested-posts API).
        // Mutating the read model directly avoids piping a new field through the
        // EditOutboxMessageCommand event flow purely for this UX patch.
        if (!balanceTooLow)
        {
            var msgDocId = MessageId.Create(monoforumPeer.PeerId, obj.MsgId).Value;
            var msgCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
            // Set the polymorphic SuggestedPost.{Accepted,Rejected,Flags} fields in place.
            var newFlags = (suggestionMessage.SuggestedPost as TSuggestedPost)?.Flags ?? 0;
            if (obj.Reject) { newFlags |= 1 << 2; } else { newFlags |= 1 << 1; }
            var update = Builders<BsonDocument>.Update
                .Set("SuggestedPost.Accepted", !obj.Reject)
                .Set("SuggestedPost.Rejected", obj.Reject)
                .Set("SuggestedPost.Flags", newFlags);
            await msgCol.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", msgDocId), update);
        }

        return null!;
    }
}
