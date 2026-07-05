using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Adds a peer to a blocklist, see <a href="https://corefork.telegram.org/api/block">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CONTACT_ID_INVALID The provided contact ID is invalid.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.block"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class BlockHandler(IPeerHelper peerHelper, IBlockCacheAppService blockCacheAppService, IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestBlock, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestBlock obj)
    {
        var target = peerHelper.GetPeer(obj.Id, input.UserId);
        if (target == null || target.PeerType == PeerType.Self || target.PeerId == input.UserId)
            return new TBoolTrue();

        // Item 22: actually persist the block so SendMessage / SetTyping can refuse the
        // sender, and notify the blocker's other sessions via updatePeerBlocked so all
        // devices show the user as blocked immediately.
        await blockCacheAppService.BlockAsync(input.UserId, target.PeerId, target.PeerType, obj.MyStoriesFrom);

        IPeer targetPeer = target.PeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = target.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = target.PeerId },
            _ => new TPeerUser { UserId = target.PeerId },
        };
        var update = new TUpdatePeerBlocked
        {
            Blocked = true,
            BlockedMyStoriesFrom = obj.MyStoriesFrom,
            PeerId = targetPeer,
        };
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            updates,
            excludeAuthKeyId: input.AuthKeyId);

        return new TBoolTrue();
    }
}
