using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Get forum topics by their ID
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_FORUM_MISSING This supergroup is not a forum.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 TOPICS_EMPTY &nbsp;
/// See <a href="https://corefork.telegram.org/method/channels.getForumTopicsByID" />
///</summary>
internal sealed class GetForumTopicsByIDHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper accessHashHelper,
    IPeerHelper peerHelper,
    IPtsHelper ptsHelper) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetForumTopicsByID, MyTelegram.Schema.Messages.IForumTopics>
{
    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestGetForumTopicsByID obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Channel);
        var peer = peerHelper.GetChannel(obj.Channel);
        var channelId = peer.PeerId;

        // Check if channel is a forum
        var channelCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (!channelDoc.Contains("Forum") || !channelDoc["Forum"].AsBoolean)
            RpcErrors.RpcErrors400.ChannelForumMissing.ThrowRpcError();

        if (obj.Topics == null || obj.Topics.Count == 0)
            RpcErrors.RpcErrors400.TopicsEmpty.ThrowRpcError();

        // Query topics by IDs
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.In("TopicId", obj.Topics)
        );

        var topicDocs = await topicsCol.Find(filter).ToListAsync();

        var topics = new TVector<IForumTopic>();
        foreach (var doc in topicDocs)
        {
            var topic = new TForumTopic
            {
                Id = doc["TopicId"].AsInt32,
                Title = doc["Title"].AsString,
                IconColor = doc.Contains("IconColor") ? doc["IconColor"].AsInt32 : 0x6FB9F0,
                IconEmojiId = doc.Contains("IconEmojiId") ? doc["IconEmojiId"].AsInt64 : 0L,
                Date = doc["Date"].AsInt32,
                TopMessage = doc.Contains("TopMessageId") ? doc["TopMessageId"].AsInt32 : doc["TopicId"].AsInt32,
                ReadInboxMaxId = 0,
                ReadOutboxMaxId = 0,
                UnreadCount = 0,
                UnreadMentionsCount = 0,
                UnreadReactionsCount = 0,
                FromId = new TPeerUser { UserId = doc["CreatorId"].AsInt64 },
                NotifySettings = new TPeerNotifySettings { Flags = 0 }
            };

            if (doc.Contains("Closed") && doc["Closed"].AsBoolean)
                topic.Closed = true;

            if (doc.Contains("Pinned") && doc["Pinned"].AsBoolean)
                topic.Pinned = true;

            if (doc.Contains("Short") && doc["Short"].AsBoolean)
                topic.Short = true;

            if (doc.Contains("Hidden") && doc["Hidden"].AsBoolean)
                topic.Hidden = true;

            topics.Add(topic);
        }

        var pts = ptsHelper.GetCachedPts(channelId);

        return new TForumTopics
        {
            Pts = pts,
            Chats = new TVector<IChat>(),
            Messages = new TVector<IMessage>(),
            Topics = topics,
            Users = new TVector<IUser>()
        };
    }
}
