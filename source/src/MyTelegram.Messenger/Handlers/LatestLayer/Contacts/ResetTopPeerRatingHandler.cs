namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Reset <a href="https://corefork.telegram.org/api/top-rating">rating</a> of top peer
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.resetTopPeerRating"/> </c></para>
/// </summary>
/// <remarks>
/// The reset is scoped to the category the client named. Clients reset one category at a time and
/// expect the others to survive: Android sends <c>topPeerCategoryCorrespondents</c> from
/// <c>removePeer</c>, <c>topPeerCategoryBotsInline</c> from <c>removeInline</c> and
/// <c>topPeerCategoryBotsApp</c> from <c>removeWebapp</c>, and iOS and telegram-tt do the same — so
/// ignoring the category means dismissing a bot from the inline strip also erases it from the
/// frequently-messaged row.
/// <para>Access: [User ✔] [Bot ✖] [Anonymous ✖]</para>
/// </remarks>
internal sealed class ResetTopPeerRatingHandler(ITopPeerRatingService ratingService, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestResetTopPeerRating, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestResetTopPeerRating obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Empty || peer.PeerId == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // A category this layer does not model resolves to null, which resets every category — losing
        // the peer everywhere is a better answer than silently resetting the wrong one.
        var category = TopPeerCategoryHelper.FromTl(obj.Category);

        await ratingService.ResetAsync(input.UserId, category, peer.PeerType, peer.PeerId);

        return new TBoolTrue();
    }
}
