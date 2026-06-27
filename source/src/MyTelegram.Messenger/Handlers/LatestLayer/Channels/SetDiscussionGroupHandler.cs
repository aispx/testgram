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
internal sealed class SetDiscussionGroupHandler(ICommandBus commandBus, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase, IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSetDiscussionGroup, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestSetDiscussionGroup obj)
    {
        if (obj.Broadcast is TInputChannel broadcastChannel)
        {
        }
        else
        {
            throw new NotImplementedException();
        }

        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Broadcast, input.UserId);
        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Group, input.UserId);
        long? groupId = null;
        if (obj.Group is TInputChannel groupChannel)
        {
        }

        switch (obj.Group)
        {
            case TInputChannel inputChannel:
                groupId = inputChannel.ChannelId;
                break;
            case TInputChannelEmpty _:
                break;
            case TInputChannelFromMessage _:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        // Get previous linked chat IDs for admin log
        var broadcastReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(broadcastChannel.ChannelId));
        var prevGroupId = broadcastReadModel?.LinkedChatId;

        // Log discussion group change in admin log for broadcast channel
        await AdminLogHelper.LogChangeLinkedChat(mongoDatabase, broadcastChannel.ChannelId, input.UserId, prevGroupId ?? 0, groupId ?? 0);

        var command = new StartSetChannelDiscussionGroupCommand(TempId.New, input.ToRequestInfo(), broadcastChannel.ChannelId, groupId);
        await commandBus.PublishAsync(command);
        return null !;
    }
}