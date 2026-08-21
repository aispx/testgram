namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns chat basic info on their IDs.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getChats"/> </c></para>
/// </summary>
/// <remarks>
/// The third bulk refresh method of the
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>,
/// alongside users.getUsers and channels.getChannels.
/// <para>
/// It takes bare ids with no access hash, because
/// <a href="https://corefork.telegram.org/api/channel#basic-groups">basic groups</a> have none.
/// Testgram stores every group as a channel (messages.createChat emits a CreateChannelCommand), so
/// no id ever lands in the basic-group range and those come back as <c>chatEmpty</c>. A channel id
/// is still resolved here, mirroring messages.getFullChat which accepts one in the same field — but
/// since the request carries no access hash to prove the caller ever received the channel, an
/// unreadable one is reported as <c>chatEmpty</c> instead of leaking its title.
/// </para>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetChatsHandler(
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetChats, MyTelegram.Schema.Messages.IChats>
{
    protected override async Task<MyTelegram.Schema.Messages.IChats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetChats obj)
    {
        if (obj.Id == null || obj.Id.Count == 0)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var requestedIds = new List<long>();
        var channelIds = new List<long>();

        foreach (var id in obj.Id!)
        {
            if (id <= 0)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }

            var peerType = peerHelper.GetPeerType(id);
            if (peerType == PeerType.User)
            {
                // A user id is never a group.
                RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
            }

            requestedIds.Add(id);
            if (peerType == PeerType.Channel)
            {
                channelIds.Add(id);
            }
        }

        var accessibleChannelIds = new List<long>();
        foreach (var channelId in channelIds.Distinct())
        {
            var channelReadModel = await channelAppService.GetAsync((long?)channelId);
            if (channelReadModel is null or { IsDeleted: true })
            {
                continue;
            }

            if (await channelAppService.HasReadAccessAsync(input.UserId, channelReadModel))
            {
                accessibleChannelIds.Add(channelId);
            }
        }

        var resolved = new Dictionary<long, IChat>();
        if (accessibleChannelIds.Count > 0)
        {
            var channelMemberReadModels =
                await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, accessibleChannelIds));
            var chats = await chatConverterService.GetChannelListAsync(input, accessibleChannelIds,
                channelMemberReadModels, layer: input.Layer);

            foreach (var chat in chats)
            {
                resolved[chat.Id] = chat;
            }
        }

        // Answer position-by-position: an id that resolved to nothing comes back as chatEmpty
        // ("group doesn't exist"), so the client can drop it from its cache.
        // See https://corefork.telegram.org/constructor/chatEmpty
        var result = new TVector<IChat>();
        foreach (var id in requestedIds)
        {
            result.Add(resolved.TryGetValue(id, out var chat) ? chat : new TChatEmpty { Id = id });
        }

        return new TChats { Chats = result };
    }
}
