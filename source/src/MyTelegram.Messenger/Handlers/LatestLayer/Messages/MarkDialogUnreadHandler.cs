using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Manually mark dialog as unread
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.markDialogUnread"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class MarkDialogUnreadHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestMarkDialogUnread, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestMarkDialogUnread obj)
    {
        switch (obj.Peer)
        {
            case TInputDialogPeer inputDialogPeer:
                var peer = peerHelper.GetPeer(inputDialogPeer.Peer, input.UserId);
                if (obj.ParentPeer != null)
                {
                    var parentPeer = peerHelper.GetPeer(obj.ParentPeer, input.UserId);
                    if (parentPeer.PeerType != PeerType.Channel || peer.PeerType != PeerType.User)
                    {
                        RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                    }

                    var monoforum = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(parentPeer.PeerId));
                    if (monoforum == null || !monoforum.IsMonoforum)
                    {
                        RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                    }

                    await MonoForumTopicStateHelper.UpsertUnreadMarkAsync(mongoDatabase, parentPeer.PeerId, input.UserId, peer, obj.Unread);
                    var updates = new TUpdates
                    {
                        Updates = new TVector<IUpdate>(new TUpdateDialogUnreadMark
                        {
                            Peer = new TDialogPeer { Peer = parentPeer.ToPeer() },
                            SavedPeerId = GetSavedDialogsHandler.ToSchemaPeer(peer),
                            Unread = obj.Unread
                        }),
                        Users = new TVector<IUser>(),
                        Chats = new TVector<IChat>(),
                        Date = DateTime.UtcNow.ToTimestamp()
                    };
                    await objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates, input.AuthKeyId);
                    break;
                }

                var command = new MarkDialogAsUnreadCommand(DialogId.Create(input.UserId, peer), obj.Unread);
                await commandBus.PublishAsync(command, CancellationToken.None);
                break;
            case TInputDialogPeerFolder inputDialogPeerFolder:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return new TBoolTrue();
    }
}
