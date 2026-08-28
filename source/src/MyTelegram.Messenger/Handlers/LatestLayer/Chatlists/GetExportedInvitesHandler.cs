using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// List all <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep links »</a> associated to a folder
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.getExportedInvites"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The chats named by the links travel with the answer, because the client draws each link as the list
/// of chats it carries and has no other source for peers it may not have loaded.</para>
/// </remarks>
internal sealed class GetExportedInvitesHandler(
    IQueryProcessor queryProcessor,
    IChatlistInviteStore chatlistInviteStore,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetExportedInvites, IExportedInvites>
{
    protected override async Task<IExportedInvites> HandleCoreAsync(IRequestInput input,
        RequestGetExportedInvites obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var readModel = await queryProcessor.ProcessAsync(
            new GetDialogFilterByIdQuery(input.UserId, chatlistFilter.FilterId));
        if (readModel == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        var inviteDocuments = await chatlistInviteStore.GetByFilterAsync(input.UserId, chatlistFilter.FilterId);

        var invites = new TVector<MyTelegram.Schema.IExportedChatlistInvite>();
        var peers = new List<Peer>();
        foreach (var document in inviteDocuments)
        {
            var invitePeers = document.ToPeers();
            peers.AddRange(invitePeers);

            invites.Add(new MyTelegram.Schema.TExportedChatlistInvite
            {
                Title = document.Title,
                Url = $"https://t.me/addlist/{document.Slug}",
                Peers = [.. invitePeers.Select(p => p.ToPeer())]
            });
        }

        var chats = new TVector<IChat>();
        var users = new TVector<IUser>();

        var channelIds = peers.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).Distinct().ToList();
        if (channelIds.Count > 0)
        {
            chats.AddRange(await chatConverterService.GetChannelListAsync(input, channelIds, layer: input.Layer));
        }

        var userIds = peers.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId).Distinct().ToList();
        if (userIds.Count > 0)
        {
            users.AddRange(await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer));
        }

        return new TExportedInvites
        {
            Invites = invites,
            Chats = chats,
            Users = users
        };
    }
}
