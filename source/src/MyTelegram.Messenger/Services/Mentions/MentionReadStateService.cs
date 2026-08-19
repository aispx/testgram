using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Mentions;

/// <summary>
/// Which <a href="https://corefork.telegram.org/api/mentions">mentions</a> of a user in one dialog have
/// already been read. The dialog's <c>unread_mentions_count</c> lives in the dialog aggregate, but the
/// per-message state that decides whether a single message still shows the @ badge is kept here.
/// </summary>
public class MentionReadStateDocument
{
    /// <summary>
    /// <c>{userId}-{peerType}-{peerId}</c>: one row per user and dialog.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }
    public int PeerType { get; set; }
    public long PeerId { get; set; }

    /// <summary>Every mention up to and including this message id has been read.</summary>
    public int ReadMaxId { get; set; }

    /// <summary>
    /// Mentions above <see cref="ReadMaxId"/> that were read one by one (messages.readMessageContents,
    /// or messages.readMentions scoped to a single forum topic). Pruned whenever the watermark moves.
    /// </summary>
    public List<int> ReadIds { get; set; } = [];

    public int Date { get; set; }
}

public interface IMentionReadStateService
{
    Task<MentionReadStateDocument?> GetAsync(long userId, Peer peer);

    /// <summary>
    /// Batch variant of <see cref="GetAsync"/>, keyed by <see cref="Key"/>, for converting a page of
    /// messages without one query per message.
    /// </summary>
    Task<IReadOnlyDictionary<string, MentionReadStateDocument>> GetManyAsync(long userId,
        IReadOnlyCollection<Peer> peers);

    /// <summary>Moves the watermark to <paramref name="readMaxId"/> and drops the ids it now covers.</summary>
    Task MarkAllReadAsync(long userId, Peer peer, int readMaxId);

    /// <summary>Marks individual mentions read, ignoring ids already covered by the watermark.</summary>
    Task MarkReadAsync(long userId, Peer peer, IReadOnlyCollection<int> messageIds);

    /// <summary>
    /// Unread mention count of every forum topic of <paramref name="channel"/>, keyed by top_msg_id.
    /// A forum shows a mention counter per topic on top of the dialog one.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetTopicMentionCountsAsync(long userId, Peer channel);

    static bool IsUnread(MentionReadStateDocument? state, int messageId)
    {
        if (state == null)
        {
            return true;
        }

        return messageId > state.ReadMaxId && !(state.ReadIds?.Contains(messageId) ?? false);
    }

    static string Key(Peer peer) => $"{(int)peer.PeerType}-{peer.PeerId}";
}

public class MentionReadStateService(IMongoDatabase database, IQueryProcessor queryProcessor)
    : IMentionReadStateService, ITransientDependency
{
    private const string CollectionName = "mention_read_state";

    private IMongoCollection<MentionReadStateDocument> Collection =>
        database.GetCollection<MentionReadStateDocument>(CollectionName);

    private static string DocumentId(long userId, Peer peer) => $"{userId}-{(int)peer.PeerType}-{peer.PeerId}";

    public Task<MentionReadStateDocument?> GetAsync(long userId, Peer peer)
    {
        return Collection
            .Find(Builders<MentionReadStateDocument>.Filter.Eq(p => p.Id, DocumentId(userId, peer)))
            .FirstOrDefaultAsync()!;
    }

    public async Task<IReadOnlyDictionary<string, MentionReadStateDocument>> GetManyAsync(long userId,
        IReadOnlyCollection<Peer> peers)
    {
        if (peers.Count == 0)
        {
            return new Dictionary<string, MentionReadStateDocument>(StringComparer.Ordinal);
        }

        var ids = peers.Select(p => DocumentId(userId, p)).Distinct().ToList();
        var documents = await Collection
            .Find(Builders<MentionReadStateDocument>.Filter.In(p => p.Id, ids))
            .ToListAsync();

        return documents.ToDictionary(p => IMentionReadStateService.Key(new Peer((PeerType)p.PeerType, p.PeerId)),
            p => p,
            StringComparer.Ordinal);
    }

    public async Task MarkAllReadAsync(long userId, Peer peer, int readMaxId)
    {
        var filter = Builders<MentionReadStateDocument>.Filter.Eq(p => p.Id, DocumentId(userId, peer));

        // $max keeps the watermark monotonic when two sessions read the dialog at once.
        var updated = await Collection.FindOneAndUpdateAsync(filter,
            Builders<MentionReadStateDocument>.Update.Combine(
                Builders<MentionReadStateDocument>.Update.Max(p => p.ReadMaxId, readMaxId),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.UserId, userId),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.PeerType, (int)peer.PeerType),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.PeerId, peer.PeerId),
                Builders<MentionReadStateDocument>.Update.Set(p => p.Date, CurrentDate())),
            new FindOneAndUpdateOptions<MentionReadStateDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        var watermark = updated?.ReadMaxId ?? readMaxId;
        if (updated?.ReadIds?.Count > 0)
        {
            await Collection.UpdateOneAsync(filter,
                Builders<MentionReadStateDocument>.Update.PullFilter(p => p.ReadIds, id => id <= watermark));
        }
    }

    public async Task MarkReadAsync(long userId, Peer peer, IReadOnlyCollection<int> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        var state = await GetAsync(userId, peer);
        var watermark = state?.ReadMaxId ?? 0;
        var ids = messageIds.Where(p => p > watermark).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        await Collection.UpdateOneAsync(
            Builders<MentionReadStateDocument>.Filter.Eq(p => p.Id, DocumentId(userId, peer)),
            Builders<MentionReadStateDocument>.Update.Combine(
                Builders<MentionReadStateDocument>.Update.AddToSetEach(p => p.ReadIds, ids),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.UserId, userId),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.PeerType, (int)peer.PeerType),
                Builders<MentionReadStateDocument>.Update.SetOnInsert(p => p.PeerId, peer.PeerId),
                Builders<MentionReadStateDocument>.Update.Set(p => p.Date, CurrentDate())),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task<IReadOnlyDictionary<int, int>> GetTopicMentionCountsAsync(long userId, Peer channel)
    {
        if (channel.PeerType != PeerType.Channel)
        {
            return new Dictionary<int, int>();
        }

        var state = await GetAsync(userId, channel);

        return await queryProcessor.ProcessAsync(new GetUnreadMentionCountByTopicQuery(
            channel.PeerId,
            userId,
            state?.ReadMaxId ?? 0,
            state?.ReadIds ?? []));
    }

    private static int CurrentDate() => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
