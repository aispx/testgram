using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Services.Impl;

/// <inheritdoc cref="IEmojiStatusResolver"/>
public class EmojiStatusResolver(
    ILayeredService<IEmojiStatusConverter> emojiStatusLayeredService,
    IMongoDatabase mongoDatabase) : IEmojiStatusResolver, ITransientDependency
{
    private readonly IMongoCollection<UniqueStarGiftDocument> _giftCollection =
        mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
    private readonly IMongoCollection<BsonDocument> _documentCollection =
        mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");

    public bool IsExpired(EmojiStatus? emojiStatus)
    {
        return emojiStatus?.Until is { } until
               && until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public async Task<IEmojiStatus?> ResolveAsync(EmojiStatus? emojiStatus, int layer = 0)
    {
        if (emojiStatus == null || IsExpired(emojiStatus))
        {
            return null;
        }

        if (emojiStatus.CollectibleId is not { } collectibleId)
        {
            return emojiStatusLayeredService.GetConverter(layer).ToEmojiStatus(emojiStatus);
        }

        var gift = await _giftCollection
            .Find(d => d.UniqueId == collectibleId && !d.Burned)
            .FirstOrDefaultAsync();
        if (gift == null)
        {
            // The gift was burned or transferred away: keep showing the emoji, drop the decoration.
            return emojiStatusLayeredService.GetConverter(layer)
                .ToEmojiStatus(emojiStatus with { CollectibleId = null });
        }

        var existingPatternIds = await GetExistingDocumentIdsAsync([GetPatternDocumentId(gift)]);

        return CollectibleEmojiStatusHelper.ToEmojiStatus(
            gift,
            emojiStatus.DocumentId,
            emojiStatus.Until,
            patternDocumentId => existingPatternIds.Contains(patternDocumentId));
    }

    public IEmojiStatus? Resolve(EmojiStatus? emojiStatus, int layer = 0)
    {
        if (emojiStatus == null || IsExpired(emojiStatus))
        {
            return null;
        }

        // The common case — a plain custom emoji — needs no database access at all.
        if (emojiStatus.CollectibleId is not { } collectibleId)
        {
            return emojiStatusLayeredService.GetConverter(layer).ToEmojiStatus(emojiStatus);
        }

        var gift = _giftCollection
            .Find(d => d.UniqueId == collectibleId && !d.Burned)
            .FirstOrDefault();
        if (gift == null)
        {
            return emojiStatusLayeredService.GetConverter(layer)
                .ToEmojiStatus(emojiStatus with { CollectibleId = null });
        }

        var patternDocumentId = GetPatternDocumentId(gift);
        var patternExists = patternDocumentId != 0
                            && _documentCollection
                                .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", new BsonInt64(patternDocumentId)))
                                .Limit(1)
                                .Any();

        return CollectibleEmojiStatusHelper.ToEmojiStatus(
            gift,
            emojiStatus.DocumentId,
            emojiStatus.Until,
            _ => patternExists);
    }

    public async Task<Dictionary<long, IEmojiStatus>> ResolveManyAsync(
        IReadOnlyCollection<KeyValuePair<long, EmojiStatus>> emojiStatuses,
        int layer = 0)
    {
        var result = new Dictionary<long, IEmojiStatus>();
        var live = emojiStatuses.Where(p => !IsExpired(p.Value)).ToList();
        if (live.Count == 0)
        {
            return result;
        }

        var collectibleIds = live
            .Where(p => p.Value.CollectibleId.HasValue)
            .Select(p => p.Value.CollectibleId!.Value)
            .Distinct()
            .ToList();

        var gifts = collectibleIds.Count == 0
            ? []
            : await _giftCollection
                .Find(Builders<UniqueStarGiftDocument>.Filter.And(
                    Builders<UniqueStarGiftDocument>.Filter.In(d => d.UniqueId, collectibleIds),
                    Builders<UniqueStarGiftDocument>.Filter.Eq(d => d.Burned, false)))
                .ToListAsync();
        var giftMap = gifts
            .GroupBy(p => p.UniqueId)
            .ToDictionary(p => p.Key, p => p.First());

        var existingPatternIds = await GetExistingDocumentIdsAsync(
            gifts.Select(GetPatternDocumentId).ToList());

        var converter = emojiStatusLayeredService.GetConverter(layer);
        foreach (var (peerId, emojiStatus) in live)
        {
            if (emojiStatus.CollectibleId is { } collectibleId
                && giftMap.TryGetValue(collectibleId, out var gift))
            {
                result[peerId] = CollectibleEmojiStatusHelper.ToEmojiStatus(
                    gift,
                    emojiStatus.DocumentId,
                    emojiStatus.Until,
                    patternDocumentId => existingPatternIds.Contains(patternDocumentId));
                continue;
            }

            var status = converter.ToEmojiStatus(
                emojiStatus.CollectibleId.HasValue ? emojiStatus with { CollectibleId = null } : emojiStatus);
            if (status != null)
            {
                result[peerId] = status;
            }
        }

        return result;
    }

    private static long GetPatternDocumentId(UniqueStarGiftDocument gift)
    {
        return gift.Attributes.FirstOrDefault(a => a.Type == "pattern")?.DocumentId ?? 0;
    }

    /// <summary>
    /// Which of the given documents actually exist, so a pattern that was never uploaded is not
    /// advertised to clients (they would render a broken emoji).
    /// </summary>
    private async Task<HashSet<long>> GetExistingDocumentIdsAsync(IReadOnlyCollection<long> documentIds)
    {
        var ids = documentIds.Where(p => p != 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var docs = await _documentCollection
            .Find(Builders<BsonDocument>.Filter.In("DocumentId",
                ids.Select(p => (BsonValue)new BsonInt64(p))))
            .Project(Builders<BsonDocument>.Projection.Include("DocumentId"))
            .ToListAsync();

        return docs
            .Where(p => p.TryGetValue("DocumentId", out var value) && !value.IsBsonNull)
            .Select(p => p["DocumentId"].ToInt64())
            .ToHashSet();
    }
}
