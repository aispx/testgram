namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Edit a group participant's <a href="https://corefork.telegram.org/api/rank">tag</a>.
/// Possible errors
/// Code Type Description
/// 400 ADMIN_RANK_EMOJI_NOT_ALLOWED An admin rank cannot contain emojis.
/// 400 ADMIN_RANK_INVALID The specified admin rank is invalid.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 PARTICIPANT_ID_INVALID The specified participant ID is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 403 RIGHT_FORBIDDEN Your admin rights do not allow you to do this.
/// 400 USER_CREATOR You've tried to edit the tag of the owner, but you're not the owner.
/// 400 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editChatParticipantRank"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditChatParticipantRankHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IQueryProcessor queryProcessor,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditChatParticipantRank, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestEditChatParticipantRank obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // Basic groups are stored as megagroups in this server (see CreateChatHandler), so a chat
        // peer resolves to the very same channel aggregate.
        if (peer.PeerType is not (PeerType.Channel or PeerType.Chat))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var participant = peerHelper.GetPeer(obj.Participant, input.UserId);
        if (participant.PeerType != PeerType.User)
        {
            RpcErrors.RpcErrors400.ParticipantIdInvalid.ThrowRpcError();
        }

        AdminRankHelper.ValidateOrThrow(obj.Rank);

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        channelReadModel.ThrowExceptionIfChannelDeleted();

        // The caller has to be in the group themselves before touching anybody's tag.
        var selfMember = await queryProcessor.ProcessAsync(
            new GetChannelMemberByUserIdQuery(peer.PeerId, input.UserId));
        if (selfMember == null || selfMember.Left || BannedRightsHelper.IsCurrentlyKicked(selfMember, CurrentDate))
        {
            RpcErrors.RpcErrors400.ChannelPrivate.ThrowRpcError();
        }

        var targetMember = await queryProcessor.ProcessAsync(
            new GetChannelMemberByUserIdQuery(peer.PeerId, participant.PeerId));
        if (targetMember == null || targetMember.Left)
        {
            RpcErrors.RpcErrors400.UserNotParticipant.ThrowRpcError();
        }

        var isSelf = participant.PeerId == input.UserId;
        var canManageRanks =
            await channelAdminRightsChecker.HasChatAdminRightAsync(peer.PeerId, input.UserId, p => p.ManageRanks);

        if (!isSelf)
        {
            // Only the owner may relabel the owner.
            if (participant.PeerId == channelReadModel!.CreatorId && input.UserId != channelReadModel.CreatorId)
            {
                RpcErrors.RpcErrors400.UserCreator.ThrowRpcError();
            }

            if (!canManageRanks)
            {
                RpcErrors.RpcErrors403.RightForbidden.ThrowRpcError();
            }
        }
        else if (!canManageRanks)
        {
            // Tags are a group feature: in a broadcast channel they are handed out by admins only.
            if (channelReadModel!.Broadcast)
            {
                RpcErrors.RpcErrors403.RightForbidden.ThrowRpcError();
            }

            var memberBannedRights = BannedRightsHelper.GetEffectiveBannedRights(selfMember, CurrentDate);
            if (!AdminRankHelper.CanEditOwnRank(channelReadModel.DefaultBannedRights, memberBannedRights))
            {
                RpcErrors.RpcErrors403.RightForbidden.ThrowRpcError();
            }
        }

        var prevRank = targetMember!.Rank ?? string.Empty;
        var newRank = obj.Rank ?? string.Empty;

        if (string.Equals(prevRank, newRank, StringComparison.Ordinal))
        {
            return EmptyUpdates();
        }

        // Only the tag is written: rights, the admin flag and the promoter stay untouched, which is
        // what lets an ordinary member carry one. channels.editAdmin writes the very same field.
        var command = new EditMemberRankCommand(
            ChannelMemberId.Create(peer.PeerId, participant.PeerId),
            input.ToRequestInfo(),
            peer.PeerId,
            participant.PeerId,
            newRank,
            prevRank);
        await commandBus.PublishAsync(command);

        await AdminLogHelper.LogParticipantEditRank(mongoDatabase, peer.PeerId, input.UserId, participant.PeerId,
            prevRank, newRank);

        // Supergroups need no dedicated update: message.from_rank carries the current tag of every
        // sender, so other clients pick the change up on their own.
        // See https://corefork.telegram.org/api/rank
        return EmptyUpdates();
    }

    private IUpdates EmptyUpdates()
    {
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
