using MyTelegram.Schema.Chatlists;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;

/// <summary>
/// Export a <a href="https://corefork.telegram.org/api/folders">folder »</a>, creating a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified folder cannot be shared.
/// 400 INVITES_TOO_MUCH Too many links exported for this folder.
/// 400 PEERS_LIST_EMPTY No peers were provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.exportChatlistInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>Exporting turns the owner's folder into a shareable one: the live service answers such a folder as
/// <c>dialogFilterChatlist</c> with <c>has_my_invites</c> (measured), and Android reads exactly that pair as
/// "I administer this shared folder" (<c>FLAG_CHATLIST</c> + <c>FLAG_CHATLIST_ADMIN</c>). Only folders built
/// from explicit chats can be shared, which is why a folder carrying type flags or exclusions is refused
/// rather than silently stripped.</para>
/// </remarks>
internal sealed class ExportChatlistInviteHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChatlistInviteStore chatlistInviteStore,
    IDialogFilterLimitResolver limitResolver,
    ILayeredService<IDialogFilterConverter> dialogFilterLayeredService)
    : RpcResultObjectHandler<RequestExportChatlistInvite, MyTelegram.Schema.Chatlists.IExportedChatlistInvite>
{
    protected override async Task<MyTelegram.Schema.Chatlists.IExportedChatlistInvite> HandleCoreAsync(
        IRequestInput input,
        RequestExportChatlistInvite obj)
    {
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        var filterId = chatlistFilter.FilterId;
        var readModel = await queryProcessor.ProcessAsync(new GetDialogFilterByIdQuery(input.UserId, filterId));
        if (readModel == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        var stored = readModel!.Filter;
        var hasTypeFlag = stored.Contacts || stored.NonContacts || stored.Groups || stored.Broadcasts || stored.Bots;
        if (hasTypeFlag || stored.ExcludePeers.Count > 0)
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        if (obj.Peers.Count == 0)
        {
            RpcErrors.RpcErrors400.PeersListEmpty.ThrowRpcError();
        }

        var inviteCount = await chatlistInviteStore.CountByFilterAsync(input.UserId, filterId);
        if (inviteCount >= await limitResolver.GetChatlistInvitesLimitAsync(input.UserId))
        {
            RpcErrors.RpcErrors400.InvitesTooMuch.ThrowRpcError();
        }

        var peers = new List<Peer>();
        foreach (var inputPeer in obj.Peers)
        {
            var peer = peerHelper.GetPeer(inputPeer, input.UserId);

            // Only groups and channels travel in a folder link: a private chat cannot be joined by a link.
            if (peer.PeerType is not (PeerType.Channel or PeerType.Chat))
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            peers.Add(peer);
        }

        var slug = GenerateSlug();
        var title = string.IsNullOrEmpty(obj.Title) ? stored.Title.Text : obj.Title;

        await chatlistInviteStore.InsertAsync(new ChatlistInviteDocument
        {
            Id = ChatlistInviteDocument.MakeId(slug),
            Slug = slug,
            CreatorUserId = input.UserId,
            FilterId = filterId,
            Title = title,
            PeerIds = [.. peers.Select(p => p.PeerId)],
            PeerTypes = [.. peers.Select(p => p.PeerType.ToString())],
            CreatedDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Revoked = false
        });

        if (!readModel.IsShareableFolder)
        {
            var shareable = stored with { IsChatlist = true };
            await commandBus.PublishAsync(
                new UpdateDialogFilterCommand(DialogFilterId.Create(input.UserId, filterId), input.ToRequestInfo(),
                    input.UserId, shareable), CancellationToken.None);
            stored = shareable;
        }

        var invite = new MyTelegram.Schema.TExportedChatlistInvite
        {
            Title = title,
            Url = $"https://t.me/addlist/{slug}",
            Peers = [.. peers.Select(p => p.ToPeer())]
        };

        return new MyTelegram.Schema.Chatlists.TExportedChatlistInvite
        {
            Filter = dialogFilterLayeredService.GetConverter(input.Layer).ToDialogFilter(stored, true),
            Invite = invite
        };
    }

    private static string GenerateSlug()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        return string.Create(16, chars, (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = source[Random.Shared.Next(source.Length)];
            }
        });
    }
}
