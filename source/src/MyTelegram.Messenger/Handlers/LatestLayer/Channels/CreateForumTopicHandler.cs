using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Create a <a href="https://corefork.telegram.org/api/forum">forum topic</a>; requires <a href="https://corefork.telegram.org/api/rights"><code>manage_topics</code> rights</a>.
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// See <a href="https://corefork.telegram.org/method/channels.createForumTopic" />
///</summary>
internal sealed class CreateForumTopicHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestCreateForumTopic, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestCreateForumTopic obj)
    {
        IInputPeer inputPeer;
        if (obj.Channel is TInputChannel inputChannel)
        {
            inputPeer = new TInputPeerChannel { ChannelId = inputChannel.ChannelId, AccessHash = inputChannel.AccessHash };
        }
        else if (obj.Channel is TInputChannelFromMessage inputChannelFromMessage)
        {
            inputPeer = new TInputPeerChannelFromMessage
            {
                Peer = inputChannelFromMessage.Peer,
                MsgId = inputChannelFromMessage.MsgId,
                ChannelId = inputChannelFromMessage.ChannelId
            };
        }
        else
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        // Check if channel is a forum
        var channelCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (!channelDoc.Contains("Forum") || !channelDoc["Forum"].AsBoolean)
            RpcErrors.RpcErrors400.ChannelForumMissing.ThrowRpcError();

        // Check manage_topics permission
        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();

        // Generate topic ID (use message ID counter)
        var countersCol = mongoDatabase.GetCollection<BsonDocument>("counters");
        var filter = Builders<BsonDocument>.Filter.Eq("_id", $"message_id_{channelId}");
        var update = Builders<BsonDocument>.Update.Inc("seq", 1);
        var options = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var result = await countersCol.FindOneAndUpdateAsync(filter, update, options);
        var topicId = result["seq"].AsInt32;

        // Create topic document
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var topicDoc = new BsonDocument
        {
            ["_id"] = $"topic-{channelId}-{topicId}",
            ["ChannelId"] = channelId,
            ["TopicId"] = topicId,
            ["Title"] = obj.Title,
            ["IconColor"] = obj.IconColor ?? 0x6FB9F0, // Default blue
            ["IconEmojiId"] = obj.IconEmojiId ?? 0L,
            ["CreatorId"] = input.UserId,
            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["Closed"] = false,
            ["Hidden"] = false,
            ["Pinned"] = false,
            ["Short"] = obj.SendAs != null, // Short topics for bots
            ["TopMessageId"] = topicId
        };

        await topicsCol.InsertOneAsync(topicDoc);

        // Log to admin log
        var forumTopic = new TForumTopic
        {
            Id = topicId,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Title = obj.Title,
            IconColor = obj.IconColor ?? 0x6FB9F0,
            IconEmojiId = obj.IconEmojiId ?? 0L,
            TopMessage = topicId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            FromId = new TPeerUser { UserId = input.UserId },
            NotifySettings = new TPeerNotifySettings(),
            Peer = new TPeerChannel { ChannelId = channelId }
        };
        await Helpers.AdminLogHelper.LogCreateTopic(mongoDatabase, channelId, input.UserId, forumTopic);

        // Send service message for topic creation
        var action = new TMessageActionTopicCreate
        {
            Title = obj.Title,
            IconColor = obj.IconColor ?? 0x6FB9F0,
            IconEmojiId = obj.IconEmojiId ?? 0L
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer(PeerType.Channel, channelId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );

        await messageAppService.SendMessageAsync([sendInput]);

        return new TUpdates
        {
            Users = new TVector<IUser>(),
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}
