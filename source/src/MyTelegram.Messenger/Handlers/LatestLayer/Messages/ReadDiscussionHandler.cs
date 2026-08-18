using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Discussion;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark a <a href="https://corefork.telegram.org/api/threads">thread</a> as read
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readDiscussion"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadDiscussionHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IThreadReadStateService threadReadStateService,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadDiscussion, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestReadDiscussion obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(peer.PeerId, obj.MsgId).Value));

        if (messageReadModel == null)
        {
            // Check if this is a forum topic ID
            if (peer.PeerType == PeerType.Channel)
            {
                var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
                var topicFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("ChannelId", peer.PeerId),
                    Builders<BsonDocument>.Filter.Eq("TopicId", obj.MsgId)
                );
                var topic = await topicsCol.Find(topicFilter).FirstOrDefaultAsync();

                // A forum topic is a thread as well, so it gets the same read pointer even when the
                // message that opened it is already gone. See https://corefork.telegram.org/api/threads
                if (topic != null)
                {
                    if (await threadReadStateService.SetInboxAsync(input.UserId, peer.PeerId, obj.MsgId, obj.ReadMaxId))
                    {
                        await PushReadInboxAsync(input, peer.PeerId, obj.MsgId, obj.ReadMaxId, null, null);
                    }

                    return new TBoolTrue();
                }
            }

            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // The read pointer belongs to the thread, not to the discussion group: reading the comments
        // of one post must leave the rest of the group unread.
        // See https://corefork.telegram.org/api/threads
        if (!await threadReadStateService.SetInboxAsync(input.UserId, peer.PeerId, obj.MsgId, obj.ReadMaxId))
        {
            return new TBoolFalse();
        }

        await PushReadInboxAsync(input,
            peer.PeerId,
            obj.MsgId,
            obj.ReadMaxId,
            // Present when the thread is the comment section of a channel post, so that clients can
            // clear the unread comment badge shown on the post itself.
            messageReadModel!.PostChannelId,
            messageReadModel.PostMessageId);

        // Everyone whose comments were just read learns about it through
        // updateReadChannelDiscussionOutbox, which drives the read marks on their own messages.
        var readAuthorUserIds = await threadReadStateService.MarkOutboxReadAsync(peer.PeerId, obj.MsgId, obj.ReadMaxId, input.UserId);
        foreach (var authorUserId in readAuthorUserIds)
        {
            var outboxUpdates = new TUpdates
            {
                Updates = new TVector<IUpdate>(new TUpdateReadChannelDiscussionOutbox
                {
                    ChannelId = peer.PeerId,
                    TopMsgId = obj.MsgId,
                    ReadMaxId = obj.ReadMaxId
                }),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp()
            };

            await objectMessageSender.PushMessageToPeerAsync(authorUserId.ToUserPeer(), outboxUpdates);
        }

        return new TBoolTrue();
    }

    /// <summary>
    /// Tells the other sessions of the reader how far this thread has been read.
    /// </summary>
    private Task PushReadInboxAsync(IRequestInput input,
        long channelId,
        int topMsgId,
        int readMaxId,
        long? broadcastId,
        int? broadcastPost)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateReadChannelDiscussionInbox
            {
                ChannelId = channelId,
                TopMsgId = topMsgId,
                ReadMaxId = readMaxId,
                BroadcastId = broadcastId,
                BroadcastPost = broadcastPost
            }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };

        return objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates, input.AuthKeyId);
    }
}
