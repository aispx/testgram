namespace MyTelegram.Messenger.Handlers.LatestLayer.Folders;
/// <summary>
/// Edit peers in <a href="https://corefork.telegram.org/api/folders#peer-folders">peer folder</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 FOLDER_ID_INVALID Invalid folder ID.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/folders.editPeerFolders"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Only folders 0 and 1 exist: "API peer folders are used only to implement the chat archive, identified
/// by folder_id 1; all other peers are in folder_id 0 by default; no other folder_id is allowed at the
/// moment." A peer parked in a folder nobody can ask for would simply disappear from every list.</para>
///
/// <para>A peer with no dialog is dropped rather than passed on: <c>DialogAggregate.UpdateDialogFolder</c>
/// asserts that the dialog exists, so the command would fail, <c>EditPeerFoldersSaga</c> would keep waiting
/// for the event it counts, and the request would never be answered at all.</para>
/// </remarks>
internal sealed class EditPeerFoldersHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Folders.RequestEditPeerFolders, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Folders.RequestEditPeerFolders obj)
    {
        if (obj.FolderPeers.Count == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var requested = new List<(IInputFolderPeer InputFolderPeer, Peer Peer)>();
        foreach (var folderPeer in obj.FolderPeers)
        {
            if (folderPeer is not TInputFolderPeer inputFolderPeer)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                continue;
            }

            if (inputFolderPeer.FolderId is not (0 or MyTelegramConsts.ArchiveFolderId))
            {
                RpcErrors.RpcErrors400.FolderIdInvalid.ThrowRpcError();
            }

            if (inputFolderPeer.Peer is TInputPeerEmpty)
            {
                continue;
            }

            requested.Add((inputFolderPeer, peerHelper.GetPeer(inputFolderPeer.Peer, input.UserId)));
        }

        if (requested.Count == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var folderPeers = new TVector<IInputFolderPeer>();
        foreach (var (inputFolderPeer, peer) in requested)
        {
            // Addressed by dialog id rather than by a list query, because the dialog being moved may sit in
            // either folder and a folder-filtered lookup would miss the one being un-archived.
            var dialog = await queryProcessor.ProcessAsync(
                new GetDialogByIdQuery(DialogId.Create(input.UserId, peer).Value));
            if (dialog is { IsDeleted: false })
            {
                folderPeers.Add(inputFolderPeer);
            }
        }

        if (folderPeers.Count == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var command = new StartEditPeerFoldersCommand(TempId.New, input.ToRequestInfo(), folderPeers);
        await commandBus.PublishAsync(command);

        return null!;
    }
}
