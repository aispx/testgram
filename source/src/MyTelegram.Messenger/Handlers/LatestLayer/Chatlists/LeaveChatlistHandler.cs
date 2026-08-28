using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Delete a folder imported using a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.leaveChatlist"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para><c>peers</c> is the whole point of the method: the client presents the folder's chats with the ones
/// from <c>chatlists.getLeaveChatlistSuggestions</c> pre-marked and the user decides which to leave together
/// with the folder. Deleting only the folder, as this used to, leaves the user in every channel the link
/// brought along with no trace of where they came from.</para>
/// </remarks>
internal sealed class LeaveChatlistHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IChatlistHiddenUpdateStore hiddenUpdateStore,
    IChatlistMembershipService membershipService)
    : RpcResultObjectHandler<RequestLeaveChatlist, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        RequestLeaveChatlist obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var filterId = chatlistFilter.FilterId;
        var folder = await queryProcessor.ProcessAsync(new GetDialogFilterByIdQuery(input.UserId, filterId));
        if (folder == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        if (!folder!.IsShareableFolder)
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        // Only chats the folder actually holds may be left through it.
        var folderPeerIds = folder.Filter.IncludePeers
            .Concat(folder.Filter.PinnedPeers)
            .Select(p => p.Peer.PeerId)
            .ToHashSet();

        var peersToLeave = new List<Peer>();
        foreach (var inputPeer in obj.Peers)
        {
            var peer = peerHelper.GetPeer(inputPeer, input.UserId);
            if (folderPeerIds.Contains(peer.PeerId) && peersToLeave.All(p => p.PeerId != peer.PeerId))
            {
                peersToLeave.Add(peer);
            }
        }

        await membershipService.LeaveAsync(input, peersToLeave);

        await commandBus.PublishAsync(
            new DeleteDialogFilterCommand(DialogFilterId.Create(input.UserId, filterId), input.ToRequestInfo()),
            CancellationToken.None);

        await hiddenUpdateStore.DeleteAsync(input.UserId, filterId);

        // The folder itself is announced by the updateDialogFilter the deletion pushes to every session,
        // including this one's other devices; the leaves are announced per channel by their own updates.
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seq = 0
        };
    }
}
