using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Handlers.Messages;

/// <summary>
/// Get forum topics of a forum
/// <para>Possible errors</para>
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getForumTopics"/> </c></para>
/// </summary>
internal sealed class GetForumTopicsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetForumTopics, MyTelegram.Schema.Messages.IForumTopics>
{
    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetForumTopics obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filterBuilder = Builders<BsonDocument>.Filter;
        var filter = filterBuilder.Eq("ChannelId", channelId);

        if (!string.IsNullOrWhiteSpace(obj.Q))
        {
            filter &= filterBuilder.Regex("Title", new BsonRegularExpression(Regex.Escape(obj.Q), "i"));
        }

        if (obj.OffsetDate > 0)
        {
            filter &= filterBuilder.Lt("Date", obj.OffsetDate);
        }

        if (obj.OffsetTopic > 0)
        {
            filter &= filterBuilder.Lt("TopicId", obj.OffsetTopic);
        }

        if (obj.OffsetId > 0)
        {
            filter &= filterBuilder.Lt("TopMessageId", obj.OffsetId);
        }

        var count = (int)await topicsCol.CountDocumentsAsync(filter);
        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var sort = Builders<BsonDocument>.Sort
            .Descending("Pinned")
            .Descending("PinOrder")
            .Descending("Date")
            .Descending("TopicId");

        var topicDocs = await topicsCol.Find(filter).Sort(sort).Limit(limit).ToListAsync();

        var topics = new TVector<IForumTopic>();
        foreach (var doc in topicDocs)
        {
            topics.Add(ForumTopicHelper.ToForumTopic(doc, channelId, input.UserId));
        }

        return new TForumTopics
        {
            Count = count,
            OrderByCreateDate = true,
            Topics = topics,
            Messages = new TVector<IMessage>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Pts = 0
        };
    }
}
