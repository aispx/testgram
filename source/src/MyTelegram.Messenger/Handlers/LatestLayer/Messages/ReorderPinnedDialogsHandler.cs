namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Reorder pinned dialogs
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.reorderPinnedDialogs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The vector may carry an <c>inputDialogPeerFolder</c> for a pinned chat archive. It holds no order of
/// its own: every client draws the archive row above the pinned chats regardless (Android's dialog comparator
/// sorts a <c>dialogFolder</c> first), so the entry is skipped and only the chats around it are reordered.</para>
/// </remarks>
internal sealed class ReorderPinnedDialogsHandler(IDialogAppService dialogAppService, IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReorderPinnedDialogs, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestReorderPinnedDialogs obj)
    {
        var peerList = new List<Peer>();
        foreach (var inputDialogPeer in obj.Order)
        {
            switch (inputDialogPeer)
            {
                case TInputDialogPeer inputDialogPeer1:
                    peerList.Add(peerHelper.GetPeer(inputDialogPeer1.Peer, input.UserId));
                    break;
                case TInputDialogPeerFolder:
                    // The archive itself; its pinned state is toggled through messages.toggleDialogPin.
                    break;
                default:
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                    break;
            }
        }

        await dialogAppService.ReorderPinnedDialogsAsync(new ReorderPinnedDialogsInput(input.UserId, peerList));
        return new TBoolTrue();
    }
}