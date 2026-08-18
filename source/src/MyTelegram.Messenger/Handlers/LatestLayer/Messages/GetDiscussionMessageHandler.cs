using MyTelegram.Messenger.Services.Discussion;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get <a href="https://corefork.telegram.org/api/threads">discussion message</a> from the <a href="https://corefork.telegram.org/api/discussion">associated discussion group</a> of a channel to show it on top of the comment section, without actually joining the group
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TOPIC_ID_INVALID The specified topic ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDiscussionMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDiscussionMessageHandler(IPeerHelper peerHelper, IQueryProcessor queryProcessor, IChannelAppService channelAppService, //ILayeredService<IChannelConverter> layeredChatService,
 IChatConverterService chatConverterService, IMessageConverterService messageConverterService, IUserConverterService userConverterService, IThreadReadStateService threadReadStateService, IPhotoAppService photoAppService) : RpcResultObjectHandler<RequestGetDiscussionMessage, IDiscussionMessage>
{
    protected override async Task<IDiscussionMessage> HandleCoreAsync(IRequestInput input, RequestGetDiscussionMessage obj)
    {
        // peer is the channel peer
        var peer = peerHelper.GetPeer(obj.Peer);
        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null!)
        {
            RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
        }

        // A discussion preview is public for a public channel/linked discussion group, but a private
        // channel's thread must not be readable by a non-member. GetPeer validates no access hash and
        // channel ids are sequential, so gate membership; the helper permits public/broadcast/linked
        // channels and only errors on a private group. See messages.getHistory.
        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
        {
            return null!;
        }

        var query = new GetDiscussionMessageQuery(channelReadModel!.Broadcast, peer.PeerId, obj.MsgId);
        var messageReadModel = await queryProcessor.ProcessAsync(query);
        if (messageReadModel == null)
        {
            return new TDiscussionMessage
            {
                Chats = new TVector<IChat>(),
                Messages = new TVector<IMessage>(),
                Users = new TVector<IUser>(),
            };
        }

        // The thread lives in the discussion group and carries its own read state; the group dialog's
        // read state describes the whole group and says nothing about this comment section.
        // See https://corefork.telegram.org/api/threads
        var threadChannelId = messageReadModel.ToPeerId;
        var topMsgId = messageReadModel.MessageId;
        var readState = await threadReadStateService.GetAsync(input.UserId, threadChannelId, topMsgId);
        var readInboxMaxId = readState?.ReadInboxMaxId ?? 0;
        var unreadCount = await threadReadStateService.GetUnreadCountAsync(threadChannelId, topMsgId, readInboxMaxId, input.UserId);

        List<long> channelIds = [peer.PeerId, threadChannelId];
        var channelReadModels = await channelAppService.GetListAsync(channelIds);

        var message = messageConverterService.ToMessage(input.UserId, messageReadModel, layer: input.Layer);
        var photoReadModels = await photoAppService.GetPhotosAsync(channelReadModels);
        var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
        var chats = chatConverterService.ToChannelList(input, channelReadModels, photoReadModels, channelMemberReadModels, layer: input.Layer);

        // messages.discussionMessage carries the users referenced by the returned messages; without
        // them the client cannot render the sender of the thread starter.
        var userIds = new List<long>();
        if (messageReadModel.SenderUserId > 0)
        {
            userIds.Add(messageReadModel.SenderUserId);
        }

        var users = userIds.Count == 0
            ? []
            : await userConverterService.GetUserListAsync(input, userIds, layer: input.Layer);

        return new TDiscussionMessage
        {
            Chats = [.. chats],
            Messages = new TVector<IMessage>(message),
            Users = [.. users],
            MaxId = messageReadModel.Reply?.MaxId,
            ReadInboxMaxId = readState?.ReadInboxMaxId,
            ReadOutboxMaxId = readState?.ReadOutboxMaxId,
            UnreadCount = unreadCount
        };
    }
}
