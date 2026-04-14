using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.Messages;

/// <summary>
/// Get forum topics by their ID
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getForumTopicsByID"/> </c></para>
/// </summary>
internal sealed class GetForumTopicsByIDHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetForumTopicsByID, MyTelegram.Schema.Messages.IForumTopics>
{
    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetForumTopicsByID obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        // Query topics by IDs
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.In("TopicId", obj.Topics)
        );

        var topicDocs = await topicsCol.Find(filter).ToListAsync();

        var topics = new TVector<IForumTopic>();
        var messages = new TVector<IMessage>();

        foreach (var doc in topicDocs)
        {
            var topicId = doc["TopicId"].AsInt32;
            var title = doc["Title"].AsString;
            var iconColor = doc.Contains("IconColor") ? doc["IconColor"].AsInt32 : 0x6FB9F0;
            var iconEmojiId = doc.Contains("IconEmojiId") ? doc["IconEmojiId"].AsInt64 : 0L;
            var creatorId = doc["CreatorId"].AsInt64;
            var date = doc["Date"].AsInt32;
            var closed = doc.Contains("Closed") && doc["Closed"].AsBoolean;
            var hidden = doc.Contains("Hidden") && doc["Hidden"].AsBoolean;
            var pinned = doc.Contains("Pinned") && doc["Pinned"].AsBoolean;

            var forumTopic = new TForumTopic
            {
                Id = topicId,
                Date = date,
                Title = title,
                IconColor = iconColor,
                IconEmojiId = iconEmojiId,
                TopMessage = topicId,
                ReadInboxMaxId = 0,
                ReadOutboxMaxId = 0,
                UnreadCount = 0,
                UnreadMentionsCount = 0,
                UnreadReactionsCount = 0,
                FromId = new TPeerUser { UserId = creatorId },
                NotifySettings = new TPeerNotifySettings(),
                Peer = new TPeerChannel { ChannelId = channelId }
            };

            if (iconEmojiId != 0)
                forumTopic.IconEmojiId = iconEmojiId;

            if (closed)
                forumTopic.Closed = true;

            if (hidden)
                forumTopic.Hidden = true;

            if (pinned)
                forumTopic.Pinned = true;

            topics.Add(forumTopic);
        }

        return new TForumTopics
        {
            Topics = topics,
            Messages = messages,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Pts = 0
        };
    }
}