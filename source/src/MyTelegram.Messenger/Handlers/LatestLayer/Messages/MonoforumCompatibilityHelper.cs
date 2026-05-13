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
        if (channel == null || !channel.IsMonoforum)
            return (channel, null);

        var topicPeer = savedPeerId ?? new Peer(PeerType.User, input.UserId);
        if (topicPeer.PeerType != PeerType.User)
            RpcErrors.RpcErrors400.ReplyToMonoforumPeerInvalid.ThrowRpcError();

        if (topicPeer.PeerId != input.UserId)
            return (channel, null);

        var requiredStars = channel.SendPaidMessagesStars ?? 0;
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
            title: "Paid message");
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
