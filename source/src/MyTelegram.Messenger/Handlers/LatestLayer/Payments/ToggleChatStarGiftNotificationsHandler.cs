using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Enables or disables the reception of notifications every time a <a href="https://corefork.telegram.org/api/gifts">gift »</a> is received by the specified channel, can only be invoked by admins with <code>post_messages</code> <a href="https://corefork.telegram.org/constructor/chatAdminRights">admin rights</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.toggleChatStarGiftNotifications"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleChatStarGiftNotificationsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestToggleChatStarGiftNotifications, IBool>
{
    /// <summary>Per admin preference: <c>{channelId}-{userId}</c>.</summary>
    private const string CollectionName = "chat_star_gift_notifications";

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestToggleChatStarGiftNotifications obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer!.PeerId));
        if (channel == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // "can only be invoked by admins with post_messages admin rights"
        if (channel!.CreatorId != input.UserId)
        {
            var admin = channel.AdminList?.FirstOrDefault(p => p.UserId == input.UserId);
            if (admin == null || !admin.AdminRights.PostMessages)
            {
                RpcErrors.RpcErrors403.ChatAdminRequired.ThrowRpcError();
            }
        }

        var id = $"{peer.PeerId}-{input.UserId}";
        await mongoDatabase.GetCollection<BsonDocument>(CollectionName).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            new BsonDocument
            {
                ["_id"] = id,
                ["channel_id"] = peer.PeerId,
                ["user_id"] = input.UserId,
                ["enabled"] = obj.Enabled,
                ["date"] = DateTime.UtcNow.ToTimestamp()
            },
            new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}