using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Mentions;

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
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IMentionReadStateService mentionReadStateService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetForumTopicsByID, MyTelegram.Schema.Messages.IForumTopics>
{
    protected override async Task<MyTelegram.Schema.Messages.IForumTopics> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetForumTopicsByID obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;

        // Same private-forum metadata leak as messages.getForumTopics: gate on membership before
        // returning topic titles/creators for a channel the caller may not be in.
        if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelId))
        {
            return null!;
        }

        // Query topics by IDs
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.In("TopicId", obj.Topics)
        );

        var topicDocs = await topicsCol.Find(filter).ToListAsync();
        var byId = topicDocs.ToDictionary(d => d["TopicId"].AsInt32);

        var mentionCounts = await mentionReadStateService.GetTopicMentionCountsAsync(input.UserId, peer);

        var topics = new TVector<IForumTopic>();
        var messages = new TVector<IMessage>();

        foreach (var topicId in obj.Topics)
        {
            if (byId.TryGetValue(topicId, out var doc))
            {
                mentionCounts.TryGetValue(topicId, out var unreadMentionsCount);
                topics.Add(ForumTopicHelper.ToForumTopic(doc, channelId, input.UserId, unreadMentionsCount));
            }
        }

        return new TForumTopics
        {
            Count = topics.Count,
            OrderByCreateDate = true,
            Topics = topics,
            Messages = messages,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Pts = 0
        };
    }
}
