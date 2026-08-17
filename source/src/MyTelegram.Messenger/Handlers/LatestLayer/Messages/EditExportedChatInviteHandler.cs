using IExportedChatInvite = MyTelegram.Schema.Messages.IExportedChatInvite;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Edit an exported chat invite
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_INVITE_PERMANENT You can't set an expiration date on permanent invite links.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 403 EDIT_BOT_INVITE_FORBIDDEN Normal users can't edit invites that were created by bots.
/// 400 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USAGE_LIMIT_INVALID The specified usage limit is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editExportedChatInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditExportedChatInviteHandler(IQueryProcessor queryProcessor, ICommandBus commandBus, IChatInviteLinkHelper chatInviteLinkHelper, IChannelAdminRightsChecker channelAdminRightsChecker, IPeerHelper peerHelper) : RpcResultObjectHandler<Schema.Messages.RequestEditExportedChatInvite, IExportedChatInvite>
{
    protected override async Task<IExportedChatInvite> HandleCoreAsync(IRequestInput input, RequestEditExportedChatInvite obj)
    {
        if (obj.Peer is not TInputPeerChannel inputPeerChannel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var link = chatInviteLinkHelper.GetHashFromLink(obj.Link);
        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteQuery(inputPeerChannel.ChannelId, link));
        if (chatInviteReadModel == null)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(inputPeerChannel.ChannelId, input.UserId, p => p.InviteUsers, RpcErrors.RpcErrors403.ChatAdminRequired);

        // A permanent link has no expiry and no usage cap; the only edit it accepts is a revoke.
        if (chatInviteReadModel!.Permanent && !obj.Revoked && (obj.ExpireDate.HasValue || obj.UsageLimit.HasValue))
        {
            RpcErrors.RpcErrors400.ChatInvitePermanent.ThrowRpcError();
        }

        if (obj.UsageLimit is <= 0)
        {
            RpcErrors.RpcErrors400.UsageLimitInvalid.ThrowRpcError();
        }

        if (obj.ExpireDate is > 0 && obj.ExpireDate.Value <= CurrentDate)
        {
            RpcErrors.RpcErrors400.ExpireDateInvalid.ThrowRpcError();
        }

        // Links created by a bot are off limits to regular users.
        if (chatInviteReadModel.AdminId != input.UserId &&
            peerHelper.IsBotUser(chatInviteReadModel.AdminId) &&
            !peerHelper.IsBotUser(input.UserId))
        {
            RpcErrors.RpcErrors403.EditBotInviteForbidden.ThrowRpcError();
        }

        // Revoking a permanent link replaces it with a freshly generated one: the saga exports the
        // new hash and the client is answered with messages.exportedChatInviteReplaced.
        var newHash = obj.Revoked ? chatInviteLinkHelper.GenerateInviteLink() : null;

        var command = new EditChatInviteCommand(ChatInviteId.Create(inputPeerChannel.ChannelId, chatInviteReadModel.InviteId),
            input.ToRequestInfo(),
            chatInviteReadModel.InviteId,
            link,
            newHash,
            input.UserId,
            obj.Title,
            obj.RequestNeeded ?? chatInviteReadModel.RequestNeeded,
            null,
            obj.ExpireDate,
            obj.UsageLimit,
            chatInviteReadModel.Permanent,
            obj.Revoked);
        await commandBus.PublishAsync(command, default);

        return null!;
    }
}
