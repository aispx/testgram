using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get a list of peers that can be used to join a group call, presenting yourself as a specific user/channel.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallJoinAs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallJoinAsHandler(
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallJoinAs, MyTelegram.Schema.Phone.IJoinAsPeers>
{
    protected override Task<MyTelegram.Schema.Phone.IJoinAsPeers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallJoinAs obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return Task.FromResult<MyTelegram.Schema.Phone.IJoinAsPeers>(null!);
        }

        var peers = new TVector<IPeer> { new TPeerUser { UserId = input.UserId } };
        if (peer.PeerId != input.UserId || peer.PeerType != PeerType.User)
        {
            peers.Add(peerHelper.ToPeer(peer));
        }

        return Task.FromResult<MyTelegram.Schema.Phone.IJoinAsPeers>(new MyTelegram.Schema.Phone.TJoinAsPeers
        {
            Peers = peers,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        });
    }
}
