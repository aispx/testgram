using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

///<summary>
/// Get <a href="https://corefork.telegram.org/api/forum">topics of a forum</a>
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_FORUM_MISSING This supergroup is not a forum.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// See <a href="https://corefork.telegram.org/method/channels.getForumTopics" />
///</summary>
internal sealed class GetForumTopicsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IPtsHelper ptsHelper) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetForumTopics, MyTelegram.Schema.Messages.IForumTopics>
{
    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Channels.RequestGetForumTopics obj)
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

        // Build filter
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filterBuilder = Builders<BsonDocument>.Filter;
        var filter = filterBuilder.Eq("ChannelId", channelId);

        if (obj.Q != null && !string.IsNullOrEmpty(obj.Q))
        {
            filter = filterBuilder.And(filter, filterBuilder.Regex("Title", new BsonRegularExpression(obj.Q, "i")));
        }

        // Query topics
        var sort = Builders<BsonDocument>.Sort.Descending("Pinned").Descending("Date");
        var query = topicsCol.Find(filter).Sort(sort);

        if (obj.OffsetDate > 0)
        {
            query = query.Skip(obj.OffsetId);
        }

        var topicDocs = await query.Limit(obj.Limit).ToListAsync();

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
