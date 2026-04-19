using MyTelegram.Messenger.Helpers;
using System.Linq;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Delete messages in a <a href="https://corefork.telegram.org/api/channel">channel/supergroup</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 420 FROZEN_METHOD_INVALID The current account is <a href="https://corefork.telegram.org/api/auth#frozen-accounts">frozen</a>, and thus cannot execute the specified action.
/// 403 MESSAGE_DELETE_FORBIDDEN You can't delete one of the messages you tried to delete, most likely because it is a service message.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.deleteMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteMessagesHandler(
    ICommandBus commandBus,
    IPtsHelper ptsHelper,
    IAccessHashHelper accessHashHelper,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IMongoDatabase mongoDatabase,
    IMessageConverterService messageConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestDeleteMessages, MyTelegram.Schema.Messages.IAffectedMessages>
{
    protected override async Task<IAffectedMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestDeleteMessages obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            await accessHashHelper.CheckAccessHashAsync(input, inputChannel.ChannelId, inputChannel.AccessHash, AccessHashType.Channel);
            if (obj.Id.Count > 0)
            {
                var ids = obj.Id.ToList();

                // Read messages from read model BEFORE deletion for admin log
                var messagesQuery = new GetMessagesQuery(
                    OwnerPeerId: inputChannel.ChannelId,
                    MessageType: MessageType.Unknown,
                    Q: null,
                    MessageIdList: ids,
                    ChannelHistoryMinId: 0,
                    Limit: ids.Count,
                    Offset: null,
                    Peer: new Peer(PeerType.Channel, inputChannel.ChannelId),
                    SelfUserId: input.UserId,
                    Pts: 0
                );
                var messages = await queryProcessor.ProcessAsync(messagesQuery);

                // Create admin log entries immediately
                if (messages.Count > 0)
                {
                    var peer = new Peer(PeerType.Channel, inputChannel.ChannelId);

                    foreach (var msg in messages)
                    {
                        var message = messageConverterService.ToMessage(input.UserId, msg, layer: input.Layer);
                        await AdminLogHelper.LogDeleteMessage(mongoDatabase, inputChannel.ChannelId, input.UserId, message);
                    }
                }

                if (!await channelAdminRightsChecker.HasChatAdminRightAsync(inputChannel.ChannelId, input.UserId, p => p.DeleteMessages))
                {
                    var firstInboxMessageId = await queryProcessor.ProcessAsync(new GetFirstInboxMessageIdByMessageIdListQuery(input.UserId, inputChannel.ChannelId, ids));
                    if (firstInboxMessageId > 0)
                    {
                        RpcErrors.RpcErrors403.MessageDeleteForbidden.ThrowRpcError();
                    }
                }

                // Delete channel post message: delete all repies
                // Delete forwarded post message: update post message channelId to 777
                var channelReadModel = await channelAppService.GetAsync(inputChannel.ChannelId);
                channelReadModel.ThrowExceptionIfChannelDeleted();
                IReadOnlyCollection<int>? repliesMessageIds = null;
                long? discussionGroupChannelId = null;
                var newTopMessageId = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(inputChannel.ChannelId, ids));
                int? newTopMessageIdForDiscussionGroup = null;
                if (channelReadModel!.Broadcast && channelReadModel.LinkedChatId.HasValue)
                {
                    discussionGroupChannelId = channelReadModel.LinkedChatId;
                    repliesMessageIds = await queryProcessor.ProcessAsync(new GetCommentsMessageIdListQuery(channelReadModel.ChannelId, ids));
                    if (repliesMessageIds.Count > 0)
                    {
                        newTopMessageIdForDiscussionGroup = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(channelReadModel.LinkedChatId.Value, repliesMessageIds.ToList()));
                    }
                }

                // Pass null for messagesToLog - will read from deleted_messages_temp instead
                var command = new StartDeleteChannelMessagesCommand(
                    TempId.New,
                    input.ToRequestInfo(),
                    inputChannel.ChannelId,
                    ids,
                    newTopMessageId,
                    newTopMessageIdForDiscussionGroup,
                    discussionGroupChannelId,
                    repliesMessageIds,
                    null);
                await commandBus.PublishAsync(command);

                return null !;
            }

            var pts = ptsHelper.GetCachedPts(input.UserId);
            return new TAffectedMessages
            {
                Pts = pts,
                PtsCount = 0
            };
        }

        throw new NotImplementedException();
    }
}