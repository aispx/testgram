using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Pin or unpin <a href="https://corefork.telegram.org/api/forum">forum topics</a>
/// See <a href="https://corefork.telegram.org/method/channels.updatePinnedForumTopic" />
///</summary>
internal sealed class UpdatePinnedForumTopicHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdatePinnedForumTopic, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestUpdatePinnedForumTopic obj)
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

        // Check manage_topics permission
        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", obj.TopicId)
        );

        var topic = await topicsCol.Find(filter).FirstOrDefaultAsync();
        if (topic == null)
            RpcErrors.RpcErrors400.TopicIdInvalid.ThrowRpcError();

        var update = Builders<BsonDocument>.Update.Set("Pinned", obj.Pinned);
        await topicsCol.UpdateOneAsync(filter, update);

        // Log to admin log
        var prevTopic = new TForumTopic
        {
            Id = obj.TopicId,
            Title = topic["Title"].AsString,
            IconColor = topic.Contains("IconColor") ? topic["IconColor"].AsInt32 : 0x6FB9F0,
            IconEmojiId = topic.Contains("IconEmojiId") ? topic["IconEmojiId"].AsInt64 : 0L,
            Date = topic["Date"].AsInt32,
            TopMessage = topic.Contains("TopMessageId") ? topic["TopMessageId"].AsInt32 : obj.TopicId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            FromId = new TPeerUser { UserId = topic["CreatorId"].AsInt64 },
            NotifySettings = new TPeerNotifySettings(),
            Peer = new TPeerChannel { ChannelId = channelId },
            Pinned = topic.Contains("Pinned") && topic["Pinned"].AsBoolean
        };

        var newTopic = new TForumTopic
        {
            Id = obj.TopicId,
            Title = topic["Title"].AsString,
            IconColor = topic.Contains("IconColor") ? topic["IconColor"].AsInt32 : 0x6FB9F0,
            IconEmojiId = topic.Contains("IconEmojiId") ? topic["IconEmojiId"].AsInt64 : 0L,
            Date = topic["Date"].AsInt32,
            TopMessage = topic.Contains("TopMessageId") ? topic["TopMessageId"].AsInt32 : obj.TopicId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            FromId = new TPeerUser { UserId = topic["CreatorId"].AsInt64 },
            NotifySettings = new TPeerNotifySettings(),
            Peer = new TPeerChannel { ChannelId = channelId },
            Pinned = obj.Pinned
        };

        await Helpers.AdminLogHelper.LogPinTopic(mongoDatabase, channelId, input.UserId, prevTopic, newTopic);

        var updatePinnedTopic = new TUpdatePinnedForumTopic
        {
            Pinned = obj.Pinned,
            Peer = new TPeerChannel { ChannelId = channelId },
            TopicId = obj.TopicId
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePinnedTopic },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate
        };
    }
}
