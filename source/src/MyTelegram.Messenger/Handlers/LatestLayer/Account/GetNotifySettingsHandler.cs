namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Gets current notification settings for a given user/group, from all users/all groups.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getNotifySettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The peer is resolved through <see cref="IPeerHelper"/>, the same call
/// <c>account.updateNotifySettings</c> makes. This used to have its own switch that read
/// <c>inputPeerSelf</c> as <c>PeerType.User</c> while the write path stored <c>PeerType.Self</c>
/// (<c>IInputPeer.ToPeer</c> normalises any peer whose id is your own to <c>Self</c>, and the Saved Messages
/// dialog id is derived from that too). Those are two different aggregate ids, so a notification sound — or
/// a mute — set for Saved Messages was written and then never read back. The three category forms carry no
/// peer id, which is the convention the write path stores them under.</para>
/// </remarks>
internal sealed class GetNotifySettingsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    ILayeredService<IPeerNotifySettingsConverter> layeredService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetNotifySettings,
        MyTelegram.Schema.IPeerNotifySettings>
{
    protected override async Task<MyTelegram.Schema.IPeerNotifySettings> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetNotifySettings obj)
    {
        var userId = input.UserId;

        var (peerType, peerId) = obj.Peer switch
        {
            TInputNotifyPeer inputNotifyPeer => AsTarget(peerHelper.GetPeer(inputNotifyPeer.Peer, userId)),
            TInputNotifyUsers => (PeerType.User, 0L),
            TInputNotifyChats => (PeerType.Chat, 0L),
            TInputNotifyBroadcasts => (PeerType.Channel, 0L),
            // Per-topic settings are not stored — the aggregate id has nowhere to put the topic, which is why
            // the write path refuses them — so a client asking for one gets the channel category, the same
            // answer it would get for a topic nobody has ever configured.
            TInputNotifyForumTopic => (PeerType.Channel, 0L),
            _ => throw new ArgumentOutOfRangeException(nameof(obj))
        };

        var id = PeerNotifySettingsId.Create(userId, peerType, peerId);
        var peerNotifySettingsReadModel = await queryProcessor.ProcessAsync(
            new GetPeerNotifySettingsByIdQuery(id.Value), CancellationToken.None);

        return layeredService.GetConverter(input.Layer).ToPeerNotifySettings(peerNotifySettingsReadModel);
    }

    private static (PeerType, long) AsTarget(Peer peer) => (peer.PeerType, peer.PeerId);
}
