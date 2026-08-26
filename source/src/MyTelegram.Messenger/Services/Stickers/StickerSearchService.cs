using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerSearchService(IMongoDatabase mongoDatabase, IFeaturedStickerSetStore featuredStickerSetStore)
    : IStickerSearchService, ITransientDependency
{
    private static Task? _indexInit;
    private static readonly object IndexInitLock = new();

    private IMongoCollection<BsonDocument> Sets =>
        mongoDatabase.GetCollection<BsonDocument>(StickerSetStore.CollectionName);

    private IMongoCollection<BsonDocument> Documents =>
        mongoDatabase.GetCollection<BsonDocument>(StickerSetMapper.DocumentCollectionName);

    public async Task<List<long>> FindByEmoticonAsync(string emoticon, bool emojiSets,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(emoticon))
        {
            return [];
        }

        await EnsureIndexesAsync();

        var rows = await Sets
            .Find(Builders<BsonDocument>.Filter.And(
                KindFilter(emojiSets),
                Builders<BsonDocument>.Filter.Eq("Packs.Emoticon", emoticon)))
            .ToListAsync(cancellationToken);

        // A set matches when any of its packs does, so the exact pack still has to be picked out of it.
        return rows
            .SelectMany(p => StickerSetPackReader.ReadPacks(p)
                .Where(pack => string.Equals(pack.Emoticon, emoticon, StringComparison.Ordinal))
                .SelectMany(pack => pack.Documents))
            .Distinct()
            .ToList();
    }

    public async Task<List<long>> FindByKeywordAsync(string query, bool emojiSets,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await EnsureIndexesAsync();

        var normalized = query.Trim().ToLowerInvariant();
        var rows = await Sets
            .Find(Builders<BsonDocument>.Filter.And(
                KindFilter(emojiSets),
                Builders<BsonDocument>.Filter.Regex("Keywords.Keyword",
                    new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(normalized)}", "i"))))
            .ToListAsync(cancellationToken);

        var result = new List<long>();
        foreach (var row in rows)
        {
            foreach (var keyword in StickerSetPackReader.ReadKeywords(row))
            {
                if (keyword.Keyword.Any(p => p.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(keyword.DocumentId);
                }
            }
        }

        return result.Distinct().ToList();
    }

    public async Task<List<long>> FindPremiumAsync(bool emojiSets, CancellationToken cancellationToken = default)
    {
        var filter = emojiSets
            ? Builders<BsonDocument>.Filter.Eq("Attributes2.Free", false)
            : Builders<BsonDocument>.Filter.Eq("VideoThumbs.Type", "f");

        var rows = await Documents
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("DocumentId"))
            .ToListAsync(cancellationToken);

        if (emojiSets)
        {
            // The filter above matches anywhere in the attribute array, so confirm the document really is a
            // custom emoji with free unset.
            rows = rows
                .Where(p => CustomEmojiAttributeHelper.TryGetCustomEmojiAttribute(p, out var attribute)
                            && !attribute.Free)
                .ToList();
        }

        return rows.ConvertAll(p => p.GetInt64("DocumentId"));
    }

    public async Task<List<BsonDocument>> SearchSetsAsync(string query, StickerSetType type,
        bool excludeFeatured, int limit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var pattern = new BsonRegularExpression(
            System.Text.RegularExpressions.Regex.Escape(query.Trim()), "i");

        var rows = await Sets
            .Find(Builders<BsonDocument>.Filter.And(
                KindFilter(type == StickerSetType.CustomEmoji),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Regex("Title", pattern),
                    Builders<BsonDocument>.Filter.Regex("ShortName", pattern),
                    Builders<BsonDocument>.Filter.Regex("Slug", pattern))))
            .Limit(limit > 0 ? limit : 20)
            .ToListAsync(cancellationToken);

        if (!excludeFeatured || rows.Count == 0)
        {
            return rows;
        }

        // exclude_featured means "do not repeat what the trending page already shows"; the catalogue has no
        // Featured flag of its own, so the trending list is the authority.
        var featured = await featuredStickerSetStore.GetFeaturedAsync(type, cancellationToken: cancellationToken);
        var featuredIds = featured.Select(p => p.GetInt64("StickerSetId")).ToHashSet();

        return rows.Where(p => !featuredIds.Contains(p.GetInt64("StickerSetId"))).ToList();
    }

    /// <summary>
    /// Custom emoji sets and sticker sets are disjoint, and mask sets belong to neither search: a mask is
    /// only ever attached to a photo, never sent as a sticker.
    /// </summary>
    private static FilterDefinition<BsonDocument> KindFilter(bool emojiSets)
    {
        return emojiSets
            ? Builders<BsonDocument>.Filter.Eq("Emojis", true)
            : Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Ne("Emojis", true),
                Builders<BsonDocument>.Filter.Ne("Masks", true));
    }

    /// <summary>Creates the indexes once; a failed attempt is not cached, so the next call retries.</summary>
    private Task EnsureIndexesAsync()
    {
        var pending = Volatile.Read(ref _indexInit);
        if (pending is { IsCompletedSuccessfully: true })
        {
            return pending;
        }

        lock (IndexInitLock)
        {
            if (_indexInit is not { IsCompletedSuccessfully: true })
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private async Task CreateIndexesAsync()
    {
        await Sets.Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("Packs.Emoticon")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("Keywords.Keyword")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("Official")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("CreatorUserId"))
        ]);
    }
}
