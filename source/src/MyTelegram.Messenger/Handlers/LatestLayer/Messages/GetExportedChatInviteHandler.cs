using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get info about a chat invite
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getExportedChatInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetExportedChatInviteHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IUserConverterService userConverterService,
    IChatInviteExportedConverterService chatInviteExportedConverterService,
    IChatInviteLinkHelper chatInviteLinkHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetExportedChatInvite, MyTelegram.Schema.Messages.IExportedChatInvite>
{
    protected override async Task<MyTelegram.Schema.Messages.IExportedChatInvite> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetExportedChatInvite obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (!channelReadModel!.AdminList.Any(p => p.UserId == input.UserId))
        {
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        }

        var link = chatInviteLinkHelper.GetHashFromLink(obj.Link);
        if (string.IsNullOrWhiteSpace(link))
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteQuery(peer.PeerId, link));
        if (chatInviteReadModel == null)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        if (chatInviteReadModel!.ExpireDate is > 0 && chatInviteReadModel.ExpireDate.Value < CurrentDate)
        {
            RpcErrors.RpcErrors400.InviteHashExpired.ThrowRpcError();
        }

        var users = await userConverterService.GetUserListAsync(input, [chatInviteReadModel.AdminId], false, false, input.Layer);
        return new TExportedChatInvite
        {
            Invite = chatInviteExportedConverterService.ToExportedChatInvite(chatInviteReadModel, input.Layer),
            Users = [..users]
        };
    }
}
