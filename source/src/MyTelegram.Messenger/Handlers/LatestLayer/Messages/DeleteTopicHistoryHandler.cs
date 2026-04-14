using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

///<summary>
/// Delete all messages in a <a href="https://corefork.telegram.org/api/forum">forum topic</a>
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 TOPIC_ID_INVALID The specified topic ID is invalid.
/// See <a href="https://corefork.telegram.org/method/messages.deleteTopicHistory" />
///</summary>
internal sealed class DeleteTopicHistoryHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteTopicHistory, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestDeleteTopicHistory obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        // Check manage_topics permission
        if (!await channelAdminRightsChecker.HasChatAdminRightAsync(channelId, input.UserId, p => p.ManageTopics))
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        // Verify topic exists
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var topicFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", obj.TopMsgId)
        );

        var topic = await topicsCol.Find(topicFilter).FirstOrDefaultAsync();
        if (topic == null)
            RpcErrors.RpcErrors400.TopicIdInvalid.ThrowRpcError();

        // Cannot delete General topic (id=1)
        if (obj.TopMsgId == 1)
            RpcErrors.RpcErrors400.TopicIdInvalid.ThrowRpcError();

        // Delete all messages in topic
        var messagesCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");
        var messageFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("OwnerPeerId", channelId),
            Builders<BsonDocument>.Filter.Eq("ReplyToMsgId", obj.TopMsgId)
        );

        var deleteResult = await messagesCol.DeleteManyAsync(messageFilter);

        // Log to admin log
        var forumTopic = new TForumTopic
        {
            Id = obj.TopMsgId,
            Title = topic["Title"].AsString,
            IconColor = topic.Contains("IconColor") ? topic["IconColor"].AsInt32 : 0x6FB9F0,
            IconEmojiId = topic.Contains("IconEmojiId") ? topic["IconEmojiId"].AsInt64 : 0L,
            Date = topic["Date"].AsInt32,
            TopMessage = topic.Contains("TopMessageId") ? topic["TopMessageId"].AsInt32 : obj.TopMsgId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            FromId = new TPeerUser { UserId = topic["CreatorId"].AsInt64 },
            NotifySettings = new TPeerNotifySettings(),
            Peer = new TPeerChannel { ChannelId = channelId }
        };
        await Helpers.AdminLogHelper.LogDeleteTopic(mongoDatabase, channelId, input.UserId, forumTopic);

        // Delete topic itself
        await topicsCol.DeleteOneAsync(topicFilter);

        return new TMessagesAffectedHistory
        {
            Pts = 0,
            PtsCount = (int)deleteResult.DeletedCount,
            Offset = 0
        };
    }
}
