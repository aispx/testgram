using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>One peer's relevance inside one category. Higher is more relevant.</summary>
public sealed record TopPeerRating(PeerType PeerType, long PeerId, double Rating);

/// <summary>
/// The <a href="https://corefork.telegram.org/api/top-rating">top peer rating</a> behind
/// <c>contacts.getTopPeers</c>.
/// </summary>
public interface ITopPeerRatingService
{
    Task<bool> IsDisabledAsync(long userId, CancellationToken cancellationToken = default);

    Task SetDisabledAsync(long userId, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rating for each requested category, most relevant first. Every requested category is present
    /// in the result, empty ones included — tdlib only clears its cached copy of the categories that
    /// came back, so an omitted category leaves stale peers in the list it hashes and
    /// <c>topPeersNotModified</c> stops working for good.
    /// </summary>
    Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> GetRatingsAsync(long userId,
        IReadOnlyCollection<TopPeerCategory> requested, int now, CancellationToken cancellationToken = default);

    Task ResetAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TopPeerRatingService(
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    ITopPeerSettingsStore settingsStore,
    ITopPeerUsageStore usageStore,
    ITopPeerRatingCache cache)
    : ITopPeerRatingService, ITransientDependency
{
    private const string MessageCollectionName = "eventflow-messagereadmodel";
    private const string BotStateCollectionName = "botfather-bot-state";

    /// <summary>
    /// The categories derived from message history. What "using" a peer means here is simply writing to
    /// it, which is what the client-side counterpart counts too (Android
    /// <c>increasePeerRaiting</c> reads <c>MAX(date) FROM messages_v2 WHERE out = 1</c>).
    /// </summary>
    private static readonly TopPeerCategory[] MessageDerivedCategories =
    [
        TopPeerCategory.Correspondents,
        TopPeerCategory.BotsPM,
        TopPeerCategory.Groups,
        TopPeerCategory.Channels
    ];

    public Task<bool> IsDisabledAsync(long userId, CancellationToken cancellationToken = default)
    {
        return settingsStore.IsDisabledAsync(userId, cancellationToken);
    }

    public async Task SetDisabledAsync(long userId, bool disabled, CancellationToken cancellationToken = default)
    {
        await settingsStore.SetDisabledAsync(userId, disabled, cancellationToken);
        cache.Invalidate(userId);
    }

    public async Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> GetRatingsAsync(long userId,
        IReadOnlyCollection<TopPeerCategory> requested, int now, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGet(userId, out var snapshot))
        {
            snapshot = await BuildSnapshotAsync(userId, now, cancellationToken);
            cache.Set(userId, snapshot);
        }

        var result = new Dictionary<TopPeerCategory, List<TopPeerRating>>(requested.Count);
        foreach (var category in requested)
        {
            result[category] = snapshot.TryGetValue(category, out var list) ? list : [];
        }

        return result;
    }

    public async Task ResetAsync(long userId, TopPeerCategory? category, PeerType peerType, long peerId,
        CancellationToken cancellationToken = default)
    {
        // A counter this server owns can simply be dropped, and then the peer is free to climb back the
        // way the method name implies. A rating derived from message history cannot: the messages are
        // still there, so the reset has to be remembered or the peer is back on the next refresh.
        await usageStore.ResetAsync(userId, category, peerType, peerId, cancellationToken);

        if (!category.HasValue || !TopPeerCategoryHelper.IsUsageTracked(category.Value))
        {
            await settingsStore.ExcludePeerAsync(userId, category, peerType, peerId, cancellationToken);
        }

        cache.Invalidate(userId);
    }

    private async Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> BuildSnapshotAsync(long userId, int now,
        CancellationToken cancellationToken)
    {
        var exclusions = await settingsStore.GetExclusionsAsync(userId, cancellationToken);

        var snapshot = await usageStore.GetRatingsAsync(userId,
            TopPeerCategoryHelper.WireOrder.Where(TopPeerCategoryHelper.IsUsageTracked).ToList(), now,
            cancellationToken);

        await FilterInlineBotsAsync(snapshot, cancellationToken);

        foreach (var pair in await ClassifyMessageHistoryAsync(userId, now, cancellationToken))
        {
            snapshot[pair.Key] = pair.Value;
        }

        foreach (var category in TopPeerCategoryHelper.WireOrder)
        {
            if (!snapshot.TryGetValue(category, out var list))
            {
                continue;
            }

            if (!exclusions.IsEmpty)
            {
                list.RemoveAll(p => exclusions.IsExcluded(category, p.PeerType, p.PeerId));
            }

            list.Sort((left, right) => right.Rating.CompareTo(left.Rating));
        }

        return snapshot;
    }

    /// <summary>
    /// Turns the caller's outgoing messages into a rating per peer, then splits the peers between the
    /// four message-derived categories by what the peer actually is.
    /// </summary>
    private async Task<Dictionary<TopPeerCategory, List<TopPeerRating>>> ClassifyMessageHistoryAsync(long userId,
        int now, CancellationToken cancellationToken)
    {
        var result = new Dictionary<TopPeerCategory, List<TopPeerRating>>();
        foreach (var category in MessageDerivedCategories)
        {
            result[category] = [];
        }

        var rated = await AggregateMessageHistoryAsync(userId, now, cancellationToken);
        if (rated.Count == 0)
        {
            return result;
        }

        var userIds = rated.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId).ToList();
        var userMap = userIds.Count == 0
            ? []
            : (await userAppService.GetListAsync(userIds)).GroupBy(p => p.UserId)
            .ToDictionary(p => p.Key, p => p.First());

        var channelIds = rated.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).ToList();
        var channelMap = channelIds.Count == 0
            ? []
            : (await channelAppService.GetListAsync(channelIds)).GroupBy(p => p.ChannelId)
            .ToDictionary(p => p.Key, p => p.First());

        foreach (var rating in rated)
        {
            switch (rating.PeerType)
            {
                // Saved Messages is not a correspondent: Android drops the self peer out of the hints
                // list on both read and write (MediaDataController.loadHints / increasePeerRaiting), so
                // serving it only puts a row in front of the clients that do not.
                case PeerType.Self:
                    continue;

                case PeerType.User when rating.PeerId != userId:
                {
                    if (!userMap.TryGetValue(rating.PeerId, out var user) || user.IsDeleted == true)
                    {
                        continue;
                    }

                    result[user.Bot ? TopPeerCategory.BotsPM : TopPeerCategory.Correspondents].Add(rating);

                    break;
                }

                case PeerType.Channel:
                {
                    if (!channelMap.TryGetValue(rating.PeerId, out var channel))
                    {
                        continue;
                    }

                    if (channel.MegaGroup)
                    {
                        result[TopPeerCategory.Groups].Add(rating);
                    }
                    else if (channel.Broadcast)
                    {
                        result[TopPeerCategory.Channels].Add(rating);
                    }

                    break;
                }

                // PeerType.Chat is deliberately absent. Testgram stores every group as a channel, so no
                // id lands in the basic-group range (see GetChatsHandler) and nothing here can build a
                // legacy `chat` object — a `peerChat` we returned would arrive without the chat it names
                // and every client would drop it.
            }
        }

        return result;
    }

    private async Task<List<TopPeerRating>> AggregateMessageHistoryAsync(long userId, int now,
        CancellationToken cancellationToken)
    {
        var match = new BsonDocument
        {
            { "OwnerPeerId", userId },
            { "Out", true },
            { "Date", new BsonDocument("$gt", now - TopPeerRatingConstants.RatingWindowSeconds) }
        };

        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
        {
            new("$match", match),
            new("$group", new BsonDocument
            {
                {
                    "_id", new BsonDocument
                    {
                        { "ToPeerType", "$ToPeerType" },
                        { "ToPeerId", "$ToPeerId" }
                    }
                },
                { "Rating", TopPeerUsageStore.RatingExpression(now) }
            })
        };

        var grouped = await mongoDatabase.GetCollection<BsonDocument>(MessageCollectionName)
            .Aggregate(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken);

        var ratings = new List<TopPeerRating>(grouped.Count);
        foreach (var doc in grouped)
        {
            var key = doc["_id"].AsBsonDocument;

            // Enums are persisted as their numeric value, not as their name.
            var peerTypeValue = key.GetValue("ToPeerType", BsonNull.Value);
            if (peerTypeValue.BsonType is not (BsonType.Int32 or BsonType.Int64))
            {
                continue;
            }

            var peerId = TopPeerBson.ReadInt64(key.GetValue("ToPeerId", BsonNull.Value));
            var rating = doc.GetValue("Rating", BsonNull.Value);
            if (peerId == 0 || !rating.IsNumeric)
            {
                continue;
            }

            ratings.Add(new TopPeerRating((PeerType)peerTypeValue.ToInt32(), peerId, rating.ToDouble()));
        }

        return ratings;
    }

    /// <summary>
    /// Keeps only bots that actually answer inline queries. Android puts this category straight into the
    /// "@" suggestion strip, so a bot without inline mode there is a suggestion that cannot work.
    /// </summary>
    private async Task FilterInlineBotsAsync(Dictionary<TopPeerCategory, List<TopPeerRating>> snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.TryGetValue(TopPeerCategory.BotsInline, out var inline) || inline.Count == 0)
        {
            return;
        }

        var botIds = inline.Select(p => p.PeerId).Distinct().ToList();
        var docs = await mongoDatabase.GetCollection<BsonDocument>(BotStateCollectionName)
            .Find(Builders<BsonDocument>.Filter.In("BotUserId", botIds))
            .ToListAsync(cancellationToken);

        var inlineEnabled = new HashSet<long>();
        foreach (var doc in docs)
        {
            if (doc.GetValue("InlineEnabled", BsonBoolean.False).ToBoolean())
            {
                inlineEnabled.Add(TopPeerBson.ReadInt64(doc.GetValue("BotUserId", BsonNull.Value)));
            }
        }

        inline.RemoveAll(p => !inlineEnabled.Contains(p.PeerId));
    }
}
