namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get and increase the view counter of a message sent or forwarded from a <a href="https://corefork.telegram.org/api/channel">channel</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getMessagesViews"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMessagesViewsHandler(
    IPeerHelper peerHelper,
    IChannelMessageViewsAppService channelMessageViewsAppService,
    IChannelAppService channelAppService,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetMessagesViews, MyTelegram.Schema.Messages.IMessageViews>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessageViews> HandleCoreAsync(IRequestInput input, RequestGetMessagesViews obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel is { Broadcast: false })
            {
                return new MyTelegram.Schema.Messages.TMessageViews
                {
                    Views = [..obj.Id.Select(_ => (MyTelegram.Schema.IMessageViews)new Schema.TMessageViews()).ToList()],
                    Chats = new TVector<IChat>(),
                    Users = new TVector<IUser>()
                };
            }

            if (obj.Id.Max() < 0)
            {
                return new MyTelegram.Schema.Messages.TMessageViews
                {
                    Views = [..obj.Id.Select(p => new Schema.TMessageViews { Views = 1 }).ToList()],
                    Chats = new TVector<IChat>(),
                    Users = new TVector<IUser>()
                };
            }

            var views = await GetMessageViewsAsync(input, peer, obj.Id.ToList(), obj.Increment);
            return new MyTelegram.Schema.Messages.TMessageViews
            {
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>(),
                Views = [..views]
            };
        }

        var nonChannelViews = await GetMessageViewsAsync(input, peer, obj.Id.ToList(), obj.Increment);
        return new MyTelegram.Schema.Messages.TMessageViews
        {
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Views = [..nonChannelViews]
        };
    }

    private async Task<IList<MyTelegram.Schema.IMessageViews>> GetMessageViewsAsync(
        IRequestInput input,
        Peer peer,
        List<int> messageIds,
        bool increment)
    {
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messages = await queryProcessor.ProcessAsync(new GetMessagesQuery(
            ownerPeerId,
            MessageType.Unknown,
            null,
            messageIds,
            0,
            0,
            null,
            null,
            0,
            0,
            0,
            null,
            null,
            false,
            false,
            false,
            null,
            0
        ));

        var messageMap = messages.ToDictionary(GetMessageId);
        var views = new List<MyTelegram.Schema.IMessageViews>(messageIds.Count);
        foreach (var messageId in messageIds)
        {
            var targetChannelId = ownerPeerId;
            var targetMessageId = messageId;
            if (messageMap.TryGetValue(messageId, out var message) &&
                TryGetForwardedChannelPostTarget(message, out var originalChannelId, out var originalMessageId))
            {
                targetChannelId = originalChannelId;
                targetMessageId = originalMessageId;
            }

            var result = await channelMessageViewsAppService.GetMessageViewsAsync(
                input.UserId,
                input.PermAuthKeyId,
                targetChannelId,
                [targetMessageId],
                increment);
            views.Add(result.FirstOrDefault() ?? new Schema.TMessageViews { Views = 0 });
        }

        return views;
    }

    private static bool TryGetForwardedChannelPostTarget(
        IMessageReadModel message,
        out long channelId,
        out int messageId)
    {
        if (message.FwdHeader?.FromId?.PeerType == PeerType.Channel &&
            message.FwdHeader.ChannelPost.HasValue)
        {
            channelId = message.FwdHeader.FromId.PeerId;
            messageId = message.FwdHeader.ChannelPost.Value;
            return true;
        }

        channelId = 0;
        messageId = 0;
        return false;
    }

    private static int GetMessageId(IMessageReadModel message)
    {
        return message.SenderMessageId != 0
            ? message.SenderMessageId
            : int.Parse(message.Id.Split('-', StringSplitOptions.RemoveEmptyEntries).Last());
    }
}
