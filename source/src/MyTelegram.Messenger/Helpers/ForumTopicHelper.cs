using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Helpers;

internal static class ForumTopicHelper
{
    public const int GeneralTopicId = 1;
    public const int DefaultIconColor = 0x6FB9F0;

    public static async Task EnsureGeneralTopicAsync(IMongoDatabase mongoDatabase, long channelId, long creatorId)
    {
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", GeneralTopicId)
        );

        var update = Builders<BsonDocument>.Update
            .SetOnInsert("_id", $"topic-{channelId}-{GeneralTopicId}")
            .SetOnInsert("ChannelId", channelId)
            .SetOnInsert("TopicId", GeneralTopicId)
            .SetOnInsert("Title", "General")
            .SetOnInsert("IconColor", DefaultIconColor)
            .SetOnInsert("IconEmojiId", 0L)
            .SetOnInsert("CreatorId", creatorId)
            .SetOnInsert("Date", DateTime.UtcNow.ToTimestamp())
            .SetOnInsert("Closed", false)
            .SetOnInsert("Hidden", false)
            .SetOnInsert("Pinned", false)
            .SetOnInsert("Short", false)
            .SetOnInsert("TitleMissing", false)
            .SetOnInsert("TopMessageId", GeneralTopicId);

        await topicsCol.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
    }

    public static async Task<BsonDocument?> GetTopicAsync(IMongoDatabase mongoDatabase, long channelId, int topicId)
    {
        var topicsCol = mongoDatabase.GetCollection<BsonDocument>("forum_topics");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
            Builders<BsonDocument>.Filter.Eq("TopicId", topicId)
        );

        return await topicsCol.Find(filter).FirstOrDefaultAsync();
    }

    public static TForumTopic ToForumTopic(BsonDocument doc, long channelId, long currentUserId = 0)
    {
        var topicId = GetInt32(doc, "TopicId", GeneralTopicId);
        var creatorId = GetInt64(doc, "CreatorId", 0);
        var iconEmojiId = GetInt64(doc, "IconEmojiId", 0);

        return new TForumTopic
        {
            My = currentUserId != 0 && creatorId == currentUserId,
            Id = topicId,
            Date = GetInt32(doc, "Date", 0),
            Peer = new TPeerChannel { ChannelId = channelId },
            Title = GetString(doc, "Title", topicId == GeneralTopicId ? "General" : string.Empty),
            IconColor = GetInt32(doc, "IconColor", DefaultIconColor),
            IconEmojiId = iconEmojiId == 0 ? null : iconEmojiId,
            TopMessage = GetInt32(doc, "TopMessageId", topicId),
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadMentionsCount = 0,
            UnreadReactionsCount = 0,
            UnreadPollVotesCount = 0,
            FromId = new TPeerUser { UserId = creatorId },
            NotifySettings = new TPeerNotifySettings(),
            Closed = GetBoolean(doc, "Closed", false),
            Hidden = GetBoolean(doc, "Hidden", false),
            Pinned = GetBoolean(doc, "Pinned", false),
            Short = GetBoolean(doc, "Short", false),
            TitleMissing = GetBoolean(doc, "TitleMissing", false)
        };
    }

    public static int? GetRequestedTopicId(IInputReplyTo? inputReplyTo)
    {
        if (inputReplyTo is not TInputReplyToMessage replyToMessage)
        {
            return null;
        }

        if (replyToMessage.TopMsgId is > GeneralTopicId)
        {
            return replyToMessage.TopMsgId;
        }

        if (replyToMessage.ReplyToMsgId > GeneralTopicId)
        {
            return replyToMessage.ReplyToMsgId;
        }

        return null;
    }

    public static async Task ValidateTopicForSendAsync(IMongoDatabase mongoDatabase, long channelId, int topicId)
    {
        if (topicId == GeneralTopicId)
        {
            return;
        }

        var topic = await GetTopicAsync(mongoDatabase, channelId, topicId);
        if (topic == null)
        {
            RpcErrors.RpcErrors406.TopicDeleted.ThrowRpcError();
        }

        if (GetBoolean(topic!, "Closed", false))
        {
            RpcErrors.RpcErrors406.TopicClosed.ThrowRpcError();
        }
    }

    private static int GetInt32(BsonDocument doc, string name, int defaultValue)
    {
        return doc.TryGetValue(name, out var value) && !value.IsBsonNull ? value.ToInt32() : defaultValue;
    }

    private static long GetInt64(BsonDocument doc, string name, long defaultValue)
    {
        return doc.TryGetValue(name, out var value) && !value.IsBsonNull ? value.ToInt64() : defaultValue;
    }

    private static string GetString(BsonDocument doc, string name, string defaultValue)
    {
        return doc.TryGetValue(name, out var value) && !value.IsBsonNull ? value.AsString : defaultValue;
    }

    private static bool GetBoolean(BsonDocument doc, string name, bool defaultValue)
    {
        return doc.TryGetValue(name, out var value) && !value.IsBsonNull ? value.ToBoolean() : defaultValue;
    }
}
