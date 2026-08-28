namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Pin/unpin a dialog
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 PEER_HISTORY_EMPTY You can't pin an empty chat with a user.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PINNED_DIALOGS_TOO_MUCH Too many pinned dialogs.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleDialogPin"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>An <c>inputDialogPeerFolder</c> pins the chat archive itself to the top of the main list. That flag is
/// what decides whether <c>messages.getDialogs</c> carries a <c>dialogFolder</c> row at all: the live service
/// sends none for an unpinned archive (measured), and Android builds its own row locally in that case.</para>
/// </remarks>
internal sealed class ToggleDialogPinHandler(ICommandBus commandBus, IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleDialogPin, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestToggleDialogPin obj)
    {
        switch (obj.Peer)
        {
            case TInputDialogPeer inputDialogPeer:
                var peer = peerHelper.GetPeer(inputDialogPeer.Peer, input.UserId);
                //var ownerUid = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
                var command = new ToggleDialogPinnedCommand(DialogId.Create(input.UserId, peer), input.ToRequestInfo(), obj.Pinned);
                await commandBus.PublishAsync(command, CancellationToken.None);
                return null!;
            case TInputDialogPeerFolder inputDialogPeerFolder:
                if (inputDialogPeerFolder.FolderId != MyTelegramConsts.ArchiveFolderId)
                {
                    RpcErrors.RpcErrors400.FolderIdInvalid.ThrowRpcError();
                }

                var pinFolderCommand = new UpdateArchivePinnedCommand(
                    DialogFilterSettingsId.Create(input.UserId),
                    input.ToRequestInfo(),
                    input.UserId,
                    obj.Pinned);
                await commandBus.PublishAsync(pinFolderCommand, CancellationToken.None);
                return null!;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                return null!;
        }
    }
}