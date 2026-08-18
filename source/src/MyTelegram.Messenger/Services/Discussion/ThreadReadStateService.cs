using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Discussion;

/// <summary>
/// How far a user has read a single <a href="https://corefork.telegram.org/api/threads">message thread</a>.
/// A thread has its own read state: reading the comments of one channel post must not mark the rest of
/// the discussion group read, and the group dialog's own read state says nothing about a thread.
/// </summary>
public class ThreadReadStateDocument
{
    /// <summary>
    /// <c>{userId}-{channelId}-{topMsgId}</c>: one row per user and thread.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }
    public long ChannelId { get; set; }

    /// <summary>Id of the message the thread starts from.</summary>
    public int TopMsgId { get; set; }

    /// <summary>Id of the latest incoming message the user has read in this thread.</summary>
    public int ReadInboxMaxId { get; set; }

    /// <summary>Id of the latest outgoing message of the user that has been read by somebody else.</summary>
    public int ReadOutboxMaxId { get; set; }

    public int Date { get; set; }
}

public interface IThreadReadStateService
{
    Task<ThreadReadStateDocument?> GetAsync(long userId, long channelId, int topMsgId);

    /// <summary>
    /// Batch variant of <see cref="GetAsync"/>, keyed by <c>{channelId}-{topMsgId}</c>, for converting a
    /// page of messages without one query per message.
    /// </summary>
    Task<IReadOnlyDictionary<string, ThreadReadStateDocument>> GetManyAsync(long userId,
        IReadOnlyCollection<(long ChannelId, int TopMsgId)> threads);

    /// <summary>
    /// Moves the inbox read pointer forward. Returns false when the stored value is already at least
    /// <paramref name="readMaxId"/>, so a repeated messages.readDiscussion is a no-op.
    /// </summary>
    Task<bool> SetInboxAsync(long userId, long channelId, int topMsgId, int readMaxId);

    /// <summary>Moves the outbox read pointer forward. Same monotonic contract as <see cref="SetInboxAsync"/>.</summary>
    Task<bool> SetOutboxAsync(long userId, long channelId, int topMsgId, int readMaxId);

    /// <summary>Number of messages in the thread above <paramref name="readInboxMaxId"/> not sent by the user.</summary>
    Task<int> GetUnreadCountAsync(long channelId, int topMsgId, int readInboxMaxId, long selfUserId);

    /// <summary>
    /// Advances the outbox pointer of everyone whose messages in the thread were just read by
    /// <paramref name="readerUserId"/>, and returns the users whose pointer actually moved so the
    /// caller can push updateReadChannelDiscussionOutbox to them.
    /// </summary>
    Task<IReadOnlyCollection<long>> MarkOutboxReadAsync(long channelId, int topMsgId, int readMaxId, long readerUserId);

    static string Key(long channelId, int topMsgId) => $"{channelId}-{topMsgId}";
}

public class ThreadReadStateService(IMongoDatabase database) : IThreadReadStateService, ITransientDependency
{
    private const string CollectionName = "thread_read_state";

    private IMongoCollection<ThreadReadStateDocument> Collection =>
        database.GetCollection<ThreadReadStateDocument>(CollectionName);

    private static string DocumentId(long userId, long channelId, int topMsgId) => $"{userId}-{channelId}-{topMsgId}";

    public Task<ThreadReadStateDocument?> GetAsync(long userId, long channelId, int topMsgId)
    {
        return Collection
            .Find(Builders<ThreadReadStateDocument>.Filter.Eq(p => p.Id, DocumentId(userId, channelId, topMsgId)))
            .FirstOrDefaultAsync()!;
    }

    public async Task<IReadOnlyDictionary<string, ThreadReadStateDocument>> GetManyAsync(long userId,
        IReadOnlyCollection<(long ChannelId, int TopMsgId)> threads)
    {
        if (threads.Count == 0)
        {
            return new Dictionary<string, ThreadReadStateDocument>(StringComparer.Ordinal);
        }

        var ids = threads.Select(p => DocumentId(userId, p.ChannelId, p.TopMsgId)).Distinct().ToList();
        var documents = await Collection
            .Find(Builders<ThreadReadStateDocument>.Filter.In(p => p.Id, ids))
            .ToListAsync();

        return documents.ToDictionary(p => IThreadReadStateService.Key(p.ChannelId, p.TopMsgId),
            p => p,
            StringComparer.Ordinal);
    }

    public Task<bool> SetInboxAsync(long userId, long channelId, int topMsgId, int readMaxId)
    {
        return SetAsync(userId, channelId, topMsgId, readMaxId, inbox: true);
    }

    public Task<bool> SetOutboxAsync(long userId, long channelId, int topMsgId, int readMaxId)
    {
        return SetAsync(userId, channelId, topMsgId, readMaxId, inbox: false);
    }

    private async Task<bool> SetAsync(long userId, long channelId, int topMsgId, int readMaxId, bool inbox)
    {
        var field = inbox
            ? Builders<ThreadReadStateDocument>.Update.Max(p => p.ReadInboxMaxId, readMaxId)
            : Builders<ThreadReadStateDocument>.Update.Max(p => p.ReadOutboxMaxId, readMaxId);

        // $max makes the pointer monotonic even when two sessions read the thread at once, and the
        // pre-update document tells us whether anything actually moved.
        var previous = await Collection.FindOneAndUpdateAsync(
            Builders<ThreadReadStateDocument>.Filter.Eq(p => p.Id, DocumentId(userId, channelId, topMsgId)),
            Builders<ThreadReadStateDocument>.Update
                .Combine(field,
                    Builders<ThreadReadStateDocument>.Update.SetOnInsert(p => p.UserId, userId),
                    Builders<ThreadReadStateDocument>.Update.SetOnInsert(p => p.ChannelId, channelId),
                    Builders<ThreadReadStateDocument>.Update.SetOnInsert(p => p.TopMsgId, topMsgId),
                    Builders<ThreadReadStateDocument>.Update.Set(p => p.Date, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())),
            new FindOneAndUpdateOptions<ThreadReadStateDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.Before
            });

        var previousMaxId = inbox ? previous?.ReadInboxMaxId : previous?.ReadOutboxMaxId;

        return (previousMaxId ?? 0) < readMaxId;
    }

    public async Task<IReadOnlyCollection<long>> MarkOutboxReadAsync(long channelId, int topMsgId, int readMaxId, long readerUserId)
    {
        var builder = Builders<BsonDocument>.Filter;
        var filter = builder.And(
            builder.Eq("OwnerPeerId", channelId),
            builder.Lte("MessageId", readMaxId),
            builder.Ne("SenderUserId", readerUserId),
            builder.Or(
                builder.Eq("ReplyToMsgId", topMsgId),
                builder.Eq("TopMsgId", topMsgId)));

        var senderUserIds = await database.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Distinct<long>("SenderUserId", filter)
            .ToListAsync();

        var affected = new List<long>();
        foreach (var senderUserId in senderUserIds)
        {
            if (senderUserId <= 0)
            {
                continue;
            }

            if (await SetOutboxAsync(senderUserId, channelId, topMsgId, readMaxId))
            {
                affected.Add(senderUserId);
            }
        }

        return affected;
    }

    public async Task<int> GetUnreadCountAsync(long channelId, int topMsgId, int readInboxMaxId, long selfUserId)
    {
        var builder = Builders<BsonDocument>.Filter;
        var filter = builder.And(
            builder.Eq("OwnerPeerId", channelId),
            builder.Gt("MessageId", readInboxMaxId),
            builder.Ne("SenderUserId", selfUserId),
            builder.Or(
                builder.Eq("ReplyToMsgId", topMsgId),
                builder.Eq("TopMsgId", topMsgId)));

        return (int)await database.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .CountDocumentsAsync(filter);
    }
}
