using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get the number of stars we have received from the specified user thanks to <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>; the received amount will be equal to the sent amount multiplied by <a href="https://corefork.telegram.org/api/config#stars-paid-message-commission-permille">stars_paid_message_commission_permille</a> divided by 1000.
/// Possible errors
/// Code Type Description
/// 400 PARENT_PEER_INVALID The specified <code>parent_peer</code> is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getPaidMessagesRevenue"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPaidMessagesRevenueHandler(
    IMongoDatabase database,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPaidMessagesRevenue, MyTelegram.Schema.Account.IPaidMessagesRevenue>
{
    protected override async Task<MyTelegram.Schema.Account.IPaidMessagesRevenue> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPaidMessagesRevenue obj)
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
            var peer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
            if (peer.PeerType == PeerType.Channel)
            {
                parentPeerId = peer.PeerId;
            }
            else
            {
                RpcErrors.RpcErrors400.ParentPeerInvalid.ThrowRpcError();
            }
        }

        // Query paid messages revenue
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
        var totalStars = revenueRecords.Sum(r => r.Contains("StarsAmount") ? r["StarsAmount"].AsInt64 : 0L);

        return new TPaidMessagesRevenue { StarsAmount = totalStars };
    }
}