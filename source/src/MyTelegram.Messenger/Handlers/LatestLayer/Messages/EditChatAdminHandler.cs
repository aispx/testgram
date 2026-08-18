namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Make a user admin in a <a href="https://corefork.telegram.org/api/channel#basic-groups">basic group</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 400 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editChatAdmin"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditChatAdminHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IPeerHelper peerHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditChatAdmin, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestEditChatAdmin obj)
    {
        // Basic groups are stored as megagroups in this server (see CreateChatHandler), so chat_id
        // addresses the very same channel aggregate as channels.editAdmin does.
        var channelReadModel = await channelAppService.GetAsync(obj.ChatId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
        }

        if (obj.UserId is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return new TBoolFalse();
        }

        var targetUserId = peerHelper.GetPeer(inputUser, input.UserId).PeerId;

        // The owner may always promote; anyone else needs add_admins.
        await channelAdminRightsChecker.CheckAdminRightAsync(obj.ChatId, input.UserId, p => p.AddAdmins);

        if (targetUserId == channelReadModel!.CreatorId)
        {
            RpcErrors.RpcErrors400.UserCreator.ThrowRpcError();
        }

        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(obj.ChatId, targetUserId));
        if (channelMember == null || channelMember.Left)
        {
            RpcErrors.RpcErrors400.UserNotParticipant.ThrowRpcError();
        }

        var isAdmin = obj.IsAdmin is TBoolTrue;

        // A basic group admin holds every right except the ones that only exist for supergroups and
        // channels: they cannot promote further admins and cannot post anonymously.
        var adminRights = new ChatAdminRights();
        if (isAdmin)
        {
            adminRights = ChatAdminRights.GetCreatorRights();
            adminRights.AddAdmins = false;
            adminRights.Anonymous = false;
            adminRights.ComputeFlag();
        }

        var prevParticipant = channelMember!.AdminRights != 0
            ? new MyTelegram.Schema.TChannelParticipantAdmin
            {
                UserId = targetUserId,
                AdminRights = new TChatAdminRights { Flags = channelMember.AdminRights },
                Rank = channelMember.Rank,
                PromotedBy = channelMember.PromotedBy ?? 0,
                Date = channelMember.Date
            }
            : (MyTelegram.Schema.IChannelParticipant)new MyTelegram.Schema.TChannelParticipant
            {
                UserId = targetUserId,
                Date = channelMember.Date
            };

        var newParticipant = isAdmin
            ? new MyTelegram.Schema.TChannelParticipantAdmin
            {
                UserId = targetUserId,
                AdminRights = adminRights.ToChatAdminRights()!,
                Rank = string.Empty,
                PromotedBy = input.UserId,
                Date = CurrentDate
            }
            : (MyTelegram.Schema.IChannelParticipant)new MyTelegram.Schema.TChannelParticipant
            {
                UserId = targetUserId,
                Date = CurrentDate
            };

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetPermanentChatInviteQuery(obj.ChatId, targetUserId));
        var command = new EditChannelAdminCommand(ChannelId.Create(obj.ChatId),
            input.ToRequestInfo(),
            input.UserId,
            input.UserId == channelReadModel.CreatorId,
            targetUserId,
            peerHelper.IsBotUser(targetUserId),
            true,
            adminRights,
            string.Empty,
            CurrentDate,
            chatInviteReadModel == null);
        await commandBus.PublishAsync(command);

        await AdminLogHelper.LogEditAdmin(mongoDatabase, obj.ChatId, input.UserId, prevParticipant, newParticipant);

        return new TBoolTrue();
    }
}
