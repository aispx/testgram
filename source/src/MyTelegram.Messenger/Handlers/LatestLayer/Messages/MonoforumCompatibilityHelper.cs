using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal static class MonoforumCompatibilityHelper
{
    public const int StarsSuggestedPostAmountMin = 5;
    public const int StarsSuggestedPostAmountMax = 100000;
    public const long TonSuggestedPostAmountMin = 10000000L;
    public const long TonSuggestedPostAmountMax = 10000000000000L;

    public static async Task<(IChannelReadModel? Channel, long? PaidMessageStars)> TryChargeMonoforumMessageAsync(
        IRequestInput input,
        Peer toPeer,
        Peer? savedPeerId,
        long? allowPaidStars,
        IQueryProcessor queryProcessor,
        IMongoDatabase mongoDatabase,
        IObjectMessageSender? objectMessageSender = null)
    {
        if (toPeer.PeerType != PeerType.Channel)
            return (null, null);

        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(toPeer.PeerId));
        if (channel == null)
            return (channel, null);

        // Item 16: when sending a paid message to a regular group/broadcast channel
        // (non-monoforum), credit the recipient channel's revenue ledger so admins can
        // see the income via payments.getStarsRevenueStats and withdraw it.
        if (!channel.IsMonoforum)
        {
            var groupRequiredStars = channel.SendPaidMessagesStars ?? 0;
            if (groupRequiredStars <= 0)
                return (channel, null);

            // Admins/creator are exempt — they don't pay to post in their own channel.
            if (channel.CreatorId == input.UserId)
                return (channel, null);
            var adminCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-chatadminreadmodel");
            var adminDoc = await adminCol.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("PeerId", toPeer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("AdminId", input.UserId)
                )).FirstOrDefaultAsync();
            if (adminDoc != null)
                return (channel, null);

            if ((allowPaidStars ?? 0) < groupRequiredStars)
                RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError((int)groupRequiredStars);

            var groupBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (groupBalance < groupRequiredStars)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -groupRequiredStars);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -groupRequiredStars,
                peerChannelId: toPeer.PeerId, title: "Paid message",
                paidMessages: (int)Math.Max(1, groupRequiredStars));
            await ChannelRevenueHelper.CreditAsync(mongoDatabase, toPeer.PeerId,
                ChannelRevenueHelper.StarsCurrency, groupRequiredStars,
                sourceUserId: input.UserId,
                title: "Paid message revenue");

            if (objectMessageSender != null)
            {
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
                await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase,
                    toPeer.PeerId, PeerType.Channel, channel.CreatorId, ChannelRevenueHelper.StarsCurrency);
            }

            return (channel, groupRequiredStars);
        }

        var topicPeer = savedPeerId ?? new Peer(PeerType.User, input.UserId);
        if (topicPeer.PeerType != PeerType.User)
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        if (topicPeer.PeerId != input.UserId)
            return (channel, null);

        // The paid-message price is configured via channels.updatePaidMessagesPrice on the
        // BROADCAST side of the monoforum pair, not on the private monoforum channel that
        // toPeer points to. We must follow LinkedMonoforumId back to the broadcast channel
        // to read SendPaidMessagesStars; falling back to the monoforum-local value keeps
        // this forward-compatible with deployments that may write it on both sides.
        var broadcastChannel = channel.LinkedMonoforumId.HasValue
            ? await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channel.LinkedMonoforumId.Value))
            : null;
        var requiredStars = (broadcastChannel?.SendPaidMessagesStars
                             ?? channel.SendPaidMessagesStars) ?? 0;
        if (requiredStars <= 0)
            return (channel, null);

        var exceptionsCol = mongoDatabase.GetCollection<BsonDocument>("paid_messages_exceptions");
        var exception = await exceptionsCol.Find(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ParentPeerId", toPeer.PeerId),
            Builders<BsonDocument>.Filter.Eq("TargetUserId", input.UserId)
        )).FirstOrDefaultAsync();

        if (exception != null)
            return (channel, null);

        if ((allowPaidStars ?? 0) < requiredStars)
            RpcErrors.RpcErrors403.AllowPaymentRequiredX.ThrowRpcError((int)requiredStars);

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < requiredStars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Debit sender stars; credit revenue to the BROADCAST channel (the public side
        // of the monoforum), not to the creator's personal wallet.
        var revenueChannelId = channel.LinkedMonoforumId ?? toPeer.PeerId;
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -requiredStars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -requiredStars,
            peerChannelId: revenueChannelId,
            title: "Paid message",
            paidMessages: (int)Math.Max(1, requiredStars));
        await ChannelRevenueHelper.CreditAsync(mongoDatabase, revenueChannelId,
            ChannelRevenueHelper.StarsCurrency, requiredStars,
            sourceUserId: input.UserId,
            title: "Paid message revenue");

        var revenueCol = mongoDatabase.GetCollection<BsonDocument>("paid_messages_revenue");
        await revenueCol.InsertOneAsync(new BsonDocument
        {
            ["ReceiverUserId"] = channel.CreatorId,
            ["SenderUserId"] = input.UserId,
            ["StarsAmount"] = requiredStars,
            ["MessageId"] = 0,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Refunded"] = false,
            ["ParentPeerId"] = toPeer.PeerId
        });

        if (objectMessageSender != null)
        {
            await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
            await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase,
                revenueChannelId, PeerType.Channel, channel.CreatorId, ChannelRevenueHelper.StarsCurrency);
        }

        return (channel, requiredStars);
    }

    public static void ValidateSuggestedPostOrThrow(ISuggestedPost? suggestedPost, bool isMonoforum)
    {
        if (suggestedPost == null)
            return;

        if (!isMonoforum)
            RpcErrors.RpcErrors400.SuggestedPostPeerInvalid.ThrowRpcError();

        switch (suggestedPost.Price)
        {
            case TStarsAmount stars when stars.Amount < StarsSuggestedPostAmountMin || stars.Amount > StarsSuggestedPostAmountMax:
                RpcErrors.RpcErrors400.SuggestedPostAmountInvalid.ThrowRpcError();
                break;
            case TStarsTonAmount ton when ton.Amount < TonSuggestedPostAmountMin || ton.Amount > TonSuggestedPostAmountMax:
                RpcErrors.RpcErrors400.SuggestedPostAmountInvalid.ThrowRpcError();
                break;
        }
    }

    public static TMessageActionSuggestedPostApproval CreateApprovalAction(
        ISuggestedPost? suggestedPost,
        bool rejected,
        bool balanceTooLow,
        string? rejectComment,
        int? scheduleDateOverride)
    {
        return new TMessageActionSuggestedPostApproval
        {
            Rejected = rejected,
            BalanceTooLow = balanceTooLow,
            RejectComment = string.IsNullOrWhiteSpace(rejectComment) ? null : rejectComment,
            ScheduleDate = scheduleDateOverride ?? (suggestedPost as TSuggestedPost)?.ScheduleDate,
            Price = (suggestedPost as TSuggestedPost)?.Price
        };
    }
}
