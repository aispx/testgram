using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Edit a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// 400 PEERS_LIST_EMPTY The specified list of peers is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.editExportedInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditExportedInviteHandler(
    IPeerHelper peerHelper,
    IChatlistInviteStore chatlistInviteStore)
    : RpcResultObjectHandler<RequestEditExportedInvite, MyTelegram.Schema.IExportedChatlistInvite>
{
    protected override async Task<MyTelegram.Schema.IExportedChatlistInvite> HandleCoreAsync(IRequestInput input,
        RequestEditExportedInvite obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        if (string.IsNullOrWhiteSpace(obj.Slug))
        {
            RpcErrors.RpcErrors400.InviteSlugEmpty.ThrowRpcError();
        }

        List<Peer>? peers = null;
        if (obj.Peers != null)
        {
            if (obj.Peers.Count == 0)
            {
                RpcErrors.RpcErrors400.PeersListEmpty.ThrowRpcError();
            }

            peers = [];
            foreach (var inputPeer in obj.Peers)
            {
                var peer = peerHelper.GetPeer(inputPeer, input.UserId);
                if (peer.PeerType is not (PeerType.Channel or PeerType.Chat))
                {
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                }

                peers.Add(peer);
            }
        }

        var invite = await chatlistInviteStore.UpdateAsync(obj.Slug, input.UserId, chatlistFilter.FilterId, obj.Title,
            peers);

        if (invite == null || invite.Revoked)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        return new MyTelegram.Schema.TExportedChatlistInvite
        {
            Title = invite!.Title,
            Url = $"https://t.me/addlist/{invite.Slug}",
            Peers = [.. invite.ToPeers().Select(p => p.ToPeer())]
        };
    }
}
