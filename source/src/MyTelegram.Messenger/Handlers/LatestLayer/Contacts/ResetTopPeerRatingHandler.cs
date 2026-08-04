using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Reset <a href="https://corefork.telegram.org/api/top-rating">rating</a> of top peer
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.resetTopPeerRating"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ResetTopPeerRatingHandler(IMongoDatabase mongoDatabase, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestResetTopPeerRating, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestResetTopPeerRating obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Empty || peer.PeerId == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await TopPeerRatingHelper.ExcludePeerAsync(mongoDatabase, input.UserId, peer.PeerType, peer.PeerId);
        return new TBoolTrue();
    }
}
