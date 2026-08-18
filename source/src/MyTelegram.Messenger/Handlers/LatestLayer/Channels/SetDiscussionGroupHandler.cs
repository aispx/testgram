namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Associate a group to a channel as <a href="https://corefork.telegram.org/api/discussion">discussion group</a> for that channel
/// Possible errors
/// Code Type Description
/// 400 BROADCAST_ID_INVALID Broadcast ID invalid.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 LINK_NOT_MODIFIED Discussion link not modified.
/// 400 MEGAGROUP_ID_INVALID Invalid supergroup ID.
/// 400 MEGAGROUP_PREHISTORY_HIDDEN Group with hidden history for new members can't be set as discussion groups.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.setDiscussionGroup"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetDiscussionGroupHandler(ICommandBus commandBus, IChannelAdminRightsChecker channelAdminRightsChecker, IChannelAppService channelAppService, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSetDiscussionGroup, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestSetDiscussionGroup obj)
    {
        var broadcastChannelId = channelAdminRightsChecker.GetChannelId(obj.Broadcast);
        if (broadcastChannelId == null)
        {
            RpcErrors.RpcErrors400.BroadcastIdInvalid.ThrowRpcError();
        }

        var broadcastReadModel = await channelAppService.GetAsync((long?)broadcastChannelId!.Value);
        if (broadcastReadModel == null || !broadcastReadModel.Broadcast)
        {
            RpcErrors.RpcErrors400.BroadcastIdInvalid.ThrowRpcError();
        }

        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(broadcastChannelId.Value, input.UserId);

        var prevGroupId = broadcastReadModel!.LinkedChatId;

        // inputChannelEmpty unlinks the current discussion group; any other constructor links the
        // supergroup it points at. See https://corefork.telegram.org/api/discussion
        long? groupId = null;
        if (obj.Group is not TInputChannelEmpty)
        {
            groupId = channelAdminRightsChecker.GetChannelId(obj.Group);
            if (groupId == null)
            {
                RpcErrors.RpcErrors400.MegagroupIdInvalid.ThrowRpcError();
            }

            var groupReadModel = await channelAppService.GetAsync((long?)groupId!.Value);
            if (groupReadModel == null || !groupReadModel.MegaGroup)
            {
                RpcErrors.RpcErrors400.MegagroupIdInvalid.ThrowRpcError();
            }

            // Access to the group's old messages must be enabled before it can host a comment
            // section, otherwise users reaching a thread from the channel would see nothing.
            if (groupReadModel!.HiddenPreHistory)
            {
                RpcErrors.RpcErrors400.MegagroupPrehistoryHidden.ThrowRpcError();
            }

            await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(groupId.Value, input.UserId);
        }

        if (prevGroupId == groupId)
        {
            RpcErrors.RpcErrors400.LinkNotModified.ThrowRpcError();
        }

        // Log the change on both sides, the way the official server does: the channel admin log
        // records the group it gained or lost, the group's log records the channel.
        await AdminLogHelper.LogChangeLinkedChat(mongoDatabase, broadcastChannelId.Value, input.UserId, prevGroupId ?? 0, groupId ?? 0);
        if (groupId.HasValue)
        {
            await AdminLogHelper.LogChangeLinkedChat(mongoDatabase, groupId.Value, input.UserId, 0, broadcastChannelId.Value);
        }

        if (prevGroupId.HasValue)
        {
            await AdminLogHelper.LogChangeLinkedChat(mongoDatabase, prevGroupId.Value, input.UserId, broadcastChannelId.Value, 0);
        }

        var command = new StartSetChannelDiscussionGroupCommand(TempId.New, input.ToRequestInfo(), broadcastChannelId.Value, groupId);
        await commandBus.PublishAsync(command);
        return null!;
    }
}
