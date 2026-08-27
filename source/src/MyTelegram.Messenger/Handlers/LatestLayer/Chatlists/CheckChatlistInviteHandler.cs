using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Obtain information about a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 INVITE_SLUG_EMPTY The specified invite slug is empty.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.checkChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Whether the folder was already imported is decided by the slug, not by the exporter's
/// <c>filter_id</c>: that number belongs to the exporter's account and matching on it reported "already
/// joined" for whichever unrelated folder of the caller happened to carry the same number.</para>
/// </remarks>
internal sealed class CheckChatlistInviteHandler(
    IQueryProcessor queryProcessor,
    IChatlistInviteStore chatlistInviteStore,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestCheckChatlistInvite, IChatlistInvite>
{
    protected override async Task<IChatlistInvite> HandleCoreAsync(IRequestInput input,
        RequestCheckChatlistInvite obj)
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

        var invitePeers = invite!.ToPeers();
        var (chats, users) = await GetPeerObjectsAsync(input, invitePeers);

        var existing = await queryProcessor.ProcessAsync(new GetImportedDialogFolderQuery(input.UserId, obj.Slug));
        if (existing != null)
        {
            var folderPeerIds = existing.Filter.IncludePeers
                .Concat(existing.Filter.PinnedPeers)
                .Select(p => p.Peer.PeerId)
                .ToHashSet();

            var alreadyPeers = new TVector<IPeer>();
            var missingPeers = new TVector<IPeer>();
            foreach (var peer in invitePeers)
            {
                if (folderPeerIds.Contains(peer.PeerId))
                {
                    alreadyPeers.Add(peer.ToPeer());
                }
                else
                {
                    missingPeers.Add(peer.ToPeer());
                }
            }

            return new TChatlistInviteAlready
            {
                FilterId = existing.Filter.Id,
                MissingPeers = missingPeers,
                AlreadyPeers = alreadyPeers,
                Chats = chats,
                Users = users
            };
        }

        return new TChatlistInvite
        {
            Title = new TTextWithEntities
            {
                Text = invite.Title,
                Entities = new TVector<IMessageEntity>()
            },
            Peers = [.. invitePeers.Select(p => p.ToPeer())],
            Chats = chats,
            Users = users
        };
    }

    private async Task<(TVector<IChat> Chats, TVector<IUser> Users)> GetPeerObjectsAsync(IRequestInput input,
        IReadOnlyCollection<Peer> peers)
    {
        var chats = new TVector<IChat>();
        var users = new TVector<IUser>();

        var channelIds = peers.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).Distinct().ToList();
        if (channelIds.Count > 0)
        {
            var channelMembers = await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
            chats.AddRange(await chatConverterService.GetChannelListAsync(input, channelIds, channelMembers,
                layer: input.Layer));
        }

        var userIds = peers.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId).Distinct().ToList();
        if (userIds.Count > 0)
        {
            users.AddRange(await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer));
        }

        return (chats, users);
    }
}
