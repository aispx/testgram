namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Edit the default banned rights of a <a href="https://corefork.telegram.org/api/channel">channel/supergroup/group</a>.
/// Possible errors
/// Code Type Description
/// 400 BANNED_RIGHTS_INVALID You provided some invalid flags in the banned rights.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 UNTIL_DATE_INVALID Invalid until date provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editChatDefaultBannedRights"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditChatDefaultBannedRightsHandler(
    ICommandBus commandBus,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditChatDefaultBannedRights, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditChatDefaultBannedRights obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // Basic groups are stored as megagroups in this server (see CreateChatHandler), so a chat
        // peer resolves to the very same channel aggregate.
        if (peer.PeerType is not (PeerType.Channel or PeerType.Chat))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // "All flags can be used except for view_messages" — a chat cannot hide itself from
        // everyone. See https://corefork.telegram.org/api/rights
        if (obj.BannedRights.ViewMessages)
        {
            RpcErrors.RpcErrors400.BannedRightsInvalid.ThrowRpcError();
        }

        if (obj.BannedRights.UntilDate < 0)
        {
            RpcErrors.RpcErrors400.UntilDateInvalid.ThrowRpcError();
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, p => p.BanUsers);

        var prevBannedRights = channelReadModel!.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights();
        var newBannedRights = ChatBannedRights.FromValue(obj.BannedRights.Flags,
            ChatBannedRights.NormalizeUntilDate(obj.BannedRights.UntilDate, CurrentDate));

        if (prevBannedRights.ToIntValue() == newBannedRights.ToIntValue() &&
            prevBannedRights.UntilDate == newBannedRights.UntilDate)
        {
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();
        }

        var command = new EditChannelDefaultBannedRightsCommand(ChannelId.Create(peer.PeerId),
            input.ToRequestInfo(), newBannedRights, input.UserId);
        await commandBus.PublishAsync(command);

        await AdminLogHelper.LogDefaultBannedRights(mongoDatabase, peer.PeerId, input.UserId,
            prevBannedRights.ToChatBannedRights()!, newBannedRights.ToChatBannedRights()!);

        return null!;
    }
}