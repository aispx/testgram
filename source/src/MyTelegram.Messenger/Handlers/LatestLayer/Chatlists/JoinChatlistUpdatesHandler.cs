using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Join channels and supergroups recently added to a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_INCLUDE_EMPTY The include_peers vector of the filter is empty.
/// 400 FILTER_INCLUDE_TOO_MUCH Too many chats in the folder.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.joinChatlistUpdates"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Only peers <c>chatlists.getChatlistUpdates</c> is currently offering may be added, so a stale client
/// cannot push arbitrary chats into the folder. The answer carries the resulting <c>updateDialogFilter</c>,
/// which is how a client learns the new peer list without re-reading every folder.</para>
/// </remarks>
internal sealed class JoinChatlistUpdatesHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChatlistInviteStore chatlistInviteStore,
    IChatlistHiddenUpdateStore hiddenUpdateStore,
    IChatlistUpdateResolver updateResolver,
    IChatlistMembershipService membershipService,
    IChatlistPeerObjectsResolver peerObjectsResolver,
    IDialogFilterLimitResolver limitResolver,
    ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    : RpcResultObjectHandler<RequestJoinChatlistUpdates, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        RequestJoinChatlistUpdates obj)
    {
        var info = await updateResolver.ResolveAsync(input.UserId, obj.Chatlist);
        var missingPeerIds = info.MissingPeers.Select(p => p.PeerId).ToHashSet();

        var addedPeers = new List<Peer>();
        foreach (var inputPeer in obj.Peers)
        {
            var peer = peerHelper.GetPeer(inputPeer, input.UserId);
            if (missingPeerIds.Contains(peer.PeerId) && addedPeers.All(p => p.PeerId != peer.PeerId))
            {
                addedPeers.Add(peer);
            }
        }

        if (addedPeers.Count == 0)
        {
            RpcErrors.RpcErrors400.FilterIncludeEmpty.ThrowRpcError();
        }

        var stored = info.Folder.Filter;
        var includePeers = stored.IncludePeers.ToList();

        if (includePeers.Count + stored.PinnedPeers.Count + addedPeers.Count >
            await limitResolver.GetChatsPerFilterLimitAsync(input.UserId))
        {
            throw new RpcException(new RpcError(400, "FILTER_INCLUDE_TOO_MUCH"));
        }

        await membershipService.JoinAsync(input, addedPeers);

        includePeers.AddRange(addedPeers.Select(p => new InputPeer(p, 0)));
        var filter = stored with { IncludePeers = includePeers };

        await commandBus.PublishAsync(
            new UpdateDialogFilterCommand(DialogFilterId.Create(input.UserId, stored.Id), input.ToRequestInfo(),
                input.UserId, filter), CancellationToken.None);

        await hiddenUpdateStore.UnhideAsync(input.UserId, stored.Id, [.. addedPeers.Select(p => p.PeerId)]);

        var hasMyInvites = await chatlistInviteStore.HasInvitesAsync(input.UserId, stored.Id);
        var update = new TUpdateDialogFilter
        {
            Id = stored.Id,
            Filter = dialogFilterLayeredService.GetConverter(input.Layer).ToDialogFilter(filter, hasMyInvites)
        };

        var (chats, users) = await peerObjectsResolver.ResolveAsync(input, addedPeers);

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = users,
            Chats = chats,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Seq = 0
        };
    }
}
