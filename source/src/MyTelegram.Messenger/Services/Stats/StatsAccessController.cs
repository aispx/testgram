namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The Access_Controller implementation. Resolves the target channel/peer and enforces the fixed
/// access-check order defined by Requirement 1.5:
/// caller-type rejection (bot/anonymous) → target resolution (<c>CHANNEL_INVALID</c>/<c>PEER_ID_INVALID</c>)
/// → channel kind (<c>BROADCAST_REQUIRED</c>/<c>MEGAGROUP_REQUIRED</c>) → joinability (<c>CHANNEL_PRIVATE</c>)
/// → admin rights (<c>CHAT_ADMIN_REQUIRED</c>). The first failing check throws its RPC error.
/// </summary>
public class StatsAccessController(
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor)
    : IStatsAccessController, ITransientDependency
{
    public async Task<IChannelReadModel> ResolveChannelForStatsAsync(
        IRequestInput input,
        IInputChannel channel,
        StatsChannelKind requiredKind,
        bool checkJoinable)
    {
        // (1) Caller-type rejection: bot or anonymous callers may not use stats methods.
        RejectBotOrAnonymousCaller(input);

        // (2) Target resolution: the channel must resolve to an existing channel read model.
        var channelReadModel = await ResolveChannelAsync(channel);

        // (3) Channel-kind check.
        switch (requiredKind)
        {
            case StatsChannelKind.BroadcastOnly when !channelReadModel.Broadcast:
                RpcErrors.RpcErrors400.BroadcastRequired.ThrowRpcError();
                break;
            case StatsChannelKind.MegagroupOnly when !channelReadModel.MegaGroup:
                RpcErrors.RpcErrors400.MegagroupRequired.ThrowRpcError();
                break;
        }

        // (4) Joinability: for broadcast stats, a private channel requires the caller to be a participant.
        if (checkJoinable && string.IsNullOrEmpty(channelReadModel.UserName))
        {
            var isMember = await channelAppService.IsChannelMemberAsync(input.UserId, channelReadModel.ChannelId);
            if (!isMember)
            {
                RpcErrors.RpcErrors400.ChannelPrivate.ThrowRpcError();
            }
        }

        // (5) Admin rights: the caller must be an admin (present in AdminList) or the creator.
        await EnsureAdminAsync(channelReadModel.ChannelId, input.UserId);

        return channelReadModel;
    }

    public async Task<Peer> ResolvePeerForStoryStatsAsync(IRequestInput input, IInputPeer peer)
    {
        // (1) Caller-type rejection: bot or anonymous callers may not use stats methods.
        RejectBotOrAnonymousCaller(input);

        // (2) Target resolution: the peer must resolve to an existing peer.
        var resolvedPeer = ResolvePeer(peer, input.UserId);

        switch (resolvedPeer.PeerType)
        {
            case PeerType.Self:
                // The caller's own stories; the caller is the author.
                return resolvedPeer;

            case PeerType.User:
            {
                var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(resolvedPeer.PeerId));
                if (user == null)
                {
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                }

                // For a user peer, only the author (the user themselves) may view story stats.
                if (resolvedPeer.PeerId != input.UserId)
                {
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                }

                return resolvedPeer;
            }

            case PeerType.Channel:
            {
                var channelReadModel = await channelAppService.GetAsync((long?)resolvedPeer.PeerId);
                if (channelReadModel == null)
                {
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                }

                // For a channel peer, the caller must be an admin (or creator) of the channel.
                await EnsureAdminAsync(channelReadModel!.ChannelId, input.UserId);

                return resolvedPeer;
            }

            default:
                // Chats and other peer types cannot own stories.
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                return null!;
        }
    }

    /// <summary>
    /// Rejects bot accounts and anonymous (unauthenticated) connections, consistent with the schema access
    /// annotation <c>[User ✔] [Bot ✖] [Anonymous ✖]</c> (Requirement 1.4).
    /// </summary>
    private void RejectBotOrAnonymousCaller(IRequestInput input)
    {
        // Anonymous connection: no authenticated user is associated with the request.
        if (input.UserId <= 0)
        {
            RpcErrors.RpcErrors400.BotMethodInvalid.ThrowRpcError();
        }

        // Bot caller: stats methods are available to user callers only.
        if (peerHelper.IsBotUser(input.UserId))
        {
            RpcErrors.RpcErrors400.BotMethodInvalid.ThrowRpcError();
        }
    }

    /// <summary>
    /// Resolves an <see cref="IInputChannel"/> to its channel read model, throwing <c>CHANNEL_INVALID</c>
    /// when the channel does not exist (Requirement 1.1).
    /// </summary>
    private async Task<IChannelReadModel> ResolveChannelAsync(IInputChannel channel)
    {
        if (channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var channelReadModel = await channelAppService.GetAsync((long?)inputChannel.ChannelId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        return channelReadModel!;
    }

    /// <summary>
    /// Resolves an <see cref="IInputPeer"/> to a <see cref="Peer"/>, throwing <c>PEER_ID_INVALID</c> when the
    /// peer cannot be resolved (Requirement 1.3a).
    /// </summary>
    private Peer ResolvePeer(IInputPeer peer, long selfUserId)
    {
        try
        {
            return peerHelper.GetPeer(peer, selfUserId);
        }
        catch (NotSupportedException)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }
    }

    /// <summary>
    /// Ensures the caller is an admin (present in the channel's admin list) or the channel creator,
    /// throwing <c>CHAT_ADMIN_REQUIRED</c> otherwise (Requirement 1.3).
    /// </summary>
    private async Task EnsureAdminAsync(long channelId, long userId)
    {
        var isAdmin = await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, userId, _ => true);
        if (!isAdmin)
        {
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        }
    }
}
