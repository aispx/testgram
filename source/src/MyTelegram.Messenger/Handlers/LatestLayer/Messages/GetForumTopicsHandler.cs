using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Mentions;
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
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IMentionReadStateService mentionReadStateService,
    IQueryProcessor queryProcessor,
    IDraftConverterService draftConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetForumTopics, MyTelegram.Schema.Messages.IForumTopics>

{
    private const int MinSearchQueryLength = 2;
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetForumTopics obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer!.PeerId;

        // Topic titles, creation dates and creator ids are private-forum metadata: the access hash
        // is not validated anywhere on this path, so without a membership gate any user could
        // enumerate the topic list of a forum they never joined. Mirrors messages.getHistory.
        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelId))
        {
            return null!;
        }

        // Topic titles, dates and creator ids are channel content: a private forum must not expose
        // them to non-members. PeerHelper.GetPeer is a pure type conversion and validates no access
        // hash, so the channel id alone is enough to reach this point.
        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelId))
        {
            return null!;
        }

        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filterBuilder = Builders<BsonDocument>.Filter;
        var filter = filterBuilder.Eq("ChannelId", channelId);
        var q = obj.Q?.Trim() ?? string.Empty;

        if (q.Length is > 0 and < MinSearchQueryLength)
        {
            RpcErrors.RpcErrors400.QueryTooShort.ThrowRpcError();
        }

        if (q.Length > 0)
        {
            filter &= filterBuilder.Regex("Title", new BsonRegularExpression(Regex.Escape(q), "i"));
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
        var limit = obj.Limit > 0 ? Math.Min(obj.Limit, MaxLimit) : MaxLimit;
        var sort = Builders<BsonDocument>.Sort
            .Descending("Pinned")
            .Descending("PinOrder")
            .Descending("Date")
            .Descending("TopicId");

        var topicDocs = await topicsCol.Find(filter).Sort(sort).Limit(limit).ToListAsync();

        var mentionCounts = await mentionReadStateService.GetTopicMentionCountsAsync(input.UserId, peer);
        var topicDrafts = await ForumTopicHelper.GetTopicDraftsAsync(queryProcessor, draftConverterService,
            input.UserId, channelId, input.Layer);

        var topics = new TVector<IForumTopic>();
        foreach (var doc in topicDocs)
        {
            var topicId = ForumTopicHelper.GetTopicId(doc);
            mentionCounts.TryGetValue(topicId, out var unreadMentionsCount);
            topicDrafts.TryGetValue(topicId, out var draft);
            topics.Add(ForumTopicHelper.ToForumTopic(doc, channelId, input.UserId, unreadMentionsCount, draft));
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
