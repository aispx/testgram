using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Import a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>, joining some or all the chats in the folder.
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_TOO_MUCH You have joined too many channels/supergroups.
/// 400 CHATLISTS_TOO_MUCH You have created too many folder links, hitting the <code>chatlist_invites_limit_default</code>/<code>chatlist_invites_limit_premium</code> <a href="https://corefork.telegram.org/api/config#chatlist-invites-limit-default">limits »</a>.
/// 400 DIALOG_FILTERS_TOO_MUCH Too many folders.
/// 400 FILTER_INCLUDE_EMPTY The include_peers vector of the filter is empty.
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.joinChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The answer has to carry the resulting <c>updateDialogFilter</c>: Android reads the new folder id out
/// of it (<c>FolderBottomSheet</c> scans the returned updates for <c>TL_updateDialogFilter</c>) and scrolls
/// to that tab, so an empty <c>updates</c> leaves the user on the chat list with no folder to look at.</para>
/// </remarks>
internal sealed class JoinChatlistInviteHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChatlistInviteStore chatlistInviteStore,
    IChatlistHiddenUpdateStore hiddenUpdateStore,
    IChatlistMembershipService membershipService,
    IChatlistPeerObjectsResolver peerObjectsResolver,
    IDialogFilterIdAllocator filterIdAllocator,
    IDialogFilterLimitResolver limitResolver,
    ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    : RpcResultObjectHandler<RequestJoinChatlistInvite, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        RequestJoinChatlistInvite obj)
    {
        if (string.IsNullOrWhiteSpace(obj.Slug))
        {
            RpcErrors.RpcErrors400.InviteSlugEmpty.ThrowRpcError();
        }

        var invite = await chatlistInviteStore.GetBySlugAsync(obj.Slug);
        if (invite == null || invite.Revoked)
        {
            RpcErrors.RpcErrors400.InviteSlugExpired.ThrowRpcError();
        }

        // Only the peers the link actually offers may be imported; anything else would let a caller build a
        // folder out of peers it has no link to.
        var invitePeerIds = invite!.PeerIds.ToHashSet();
        var selectedPeers = new List<Peer>();
        foreach (var inputPeer in obj.Peers)
        {
            var peer = peerHelper.GetPeer(inputPeer, input.UserId);
            if (invitePeerIds.Contains(peer.PeerId) && selectedPeers.All(p => p.PeerId != peer.PeerId))
            {
                selectedPeers.Add(peer);
            }
        }

        if (selectedPeers.Count == 0)
        {
            RpcErrors.RpcErrors400.FilterIncludeEmpty.ThrowRpcError();
        }

        var existing = await queryProcessor.ProcessAsync(new GetImportedDialogFolderQuery(input.UserId, obj.Slug));
        var filterId = existing?.Filter.Id ?? 0;
        var includePeers = existing?.Filter.IncludePeers.ToList() ?? [];
        var pinnedPeers = existing?.Filter.PinnedPeers.ToList() ?? [];

        if (existing == null)
        {
            var filters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(input.UserId));

            if (filters.Count(p => p.IsShareableFolder) >= await limitResolver.GetJoinedChatlistLimitAsync(input.UserId))
            {
                RpcErrors.RpcErrors400.ChatlistsTooMuch.ThrowRpcError();
            }

            if (filters.Count >= await limitResolver.GetFilterLimitAsync(input.UserId))
            {
                throw new RpcException(new RpcError(400, "DIALOG_FILTERS_TOO_MUCH"));
            }

            filterId = await filterIdAllocator.AllocateAsync(input.UserId);
        }

        var known = includePeers.Select(p => p.Peer.PeerId).Concat(pinnedPeers.Select(p => p.Peer.PeerId)).ToHashSet();
        var addedPeers = selectedPeers.Where(p => !known.Contains(p.PeerId)).ToList();

        var chatsLimit = await limitResolver.GetChatsPerFilterLimitAsync(input.UserId);
        if (includePeers.Count + pinnedPeers.Count + addedPeers.Count > chatsLimit)
        {
            throw new RpcException(new RpcError(400, "FILTER_INCLUDE_TOO_MUCH"));
        }

        await membershipService.JoinAsync(input, addedPeers);

        includePeers.AddRange(addedPeers.Select(p => new InputPeer(p, 0)));

        var filter = new DialogFilter(
            filterId,
            false, false, false, false, false, false, false, false,
            existing?.Filter.TitleNoAnimate ?? false,
            existing?.Filter.Title ?? new TTextWithEntities
            {
                Text = invite.Title,
                Entities = new TVector<IMessageEntity>()
            },
            existing?.Filter.Emoticon,
            existing?.Filter.Color,
            pinnedPeers,
            includePeers,
            [],
            true,
            obj.Slug);

        await commandBus.PublishAsync(
            new UpdateDialogFilterCommand(DialogFilterId.Create(input.UserId, filterId), input.ToRequestInfo(),
                input.UserId, filter), CancellationToken.None);

        // Peers that just made it into the folder are no longer pending updates.
        await hiddenUpdateStore.UnhideAsync(input.UserId, filterId, [.. addedPeers.Select(p => p.PeerId)]);

        var hasMyInvites = await chatlistInviteStore.HasInvitesAsync(input.UserId, filterId);
        var update = new TUpdateDialogFilter
        {
            Id = filterId,
            Filter = dialogFilterLayeredService.GetConverter(input.Layer).ToDialogFilter(filter, hasMyInvites)
        };

        var (chats, users) = await peerObjectsResolver.ResolveAsync(input, selectedPeers);

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
