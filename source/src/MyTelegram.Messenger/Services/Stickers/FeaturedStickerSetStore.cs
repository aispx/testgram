using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class FeaturedStickerSetStore(IMongoDatabase mongoDatabase, IStickerSetStore stickerSetStore)
    : IFeaturedStickerSetStore, ITransientDependency
{
    public const string RegularCollectionName = "featured_sticker_sets";

    /// <summary>
    /// Custom emoji sets were already served from their own collection before the normal ones existed;
    /// the name is kept so no re-seed is needed.
    /// </summary>
    public const string EmojiCollectionName = "featured_emoji_sticker_sets";

    public const string ReadCollectionName = "read_featured_sticker_sets";

    /// <summary>How many official sets stand in for a trending list that was never seeded.</summary>
    private const int FallbackLimit = 20;

    private IMongoCollection<FeaturedStickerSetDocument> Collection(StickerSetType type) =>
        mongoDatabase.GetCollection<FeaturedStickerSetDocument>(type == StickerSetType.CustomEmoji
            ? EmojiCollectionName
            : RegularCollectionName);

    private IMongoCollection<ReadFeaturedStickerSetsDocument> ReadCollection =>
        mongoDatabase.GetCollection<ReadFeaturedStickerSetsDocument>(ReadCollectionName);

    public async Task<List<BsonDocument>> GetFeaturedAsync(StickerSetType type, int offset = 0, int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadAsync(type, false, offset, limit, cancellationToken);
        if (rows.Count > 0)
        {
            return rows;
        }

        return await stickerSetStore.FindOfficialAsync(type, limit > 0 ? limit : FallbackLimit, cancellationToken);
    }

    public Task<List<BsonDocument>> GetOldFeaturedAsync(StickerSetType type, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        return LoadAsync(type, true, offset, limit, cancellationToken);
    }

    public async Task<int> CountOldFeaturedAsync(StickerSetType type,
        CancellationToken cancellationToken = default)
    {
        var count = await Collection(type).CountDocumentsAsync(
            Builders<FeaturedStickerSetDocument>.Filter.Eq(p => p.Archived, true),
            cancellationToken: cancellationToken);

        return (int)count;
    }

    public async Task<HashSet<long>> GetReadIdsAsync(long userId, StickerSetType type,
        CancellationToken cancellationToken = default)
    {
        var row = await ReadCollection
            .Find(Builders<ReadFeaturedStickerSetsDocument>.Filter.Eq(p => p.Id,
                ReadFeaturedStickerSetsDocument.MakeId(userId, type)))
            .FirstOrDefaultAsync(cancellationToken);

        return row == null ? [] : [..row.ReadSetIds];
    }

    public async Task<bool> MarkReadAsync(long userId, StickerSetType type,
        IReadOnlyCollection<long> stickerSetIds, CancellationToken cancellationToken = default)
    {
        if (stickerSetIds.Count == 0)
        {
            return false;
        }

        var id = ReadFeaturedStickerSetsDocument.MakeId(userId, type);
        var existing = await GetReadIdsAsync(userId, type, cancellationToken);
        if (stickerSetIds.All(existing.Contains))
        {
            return false;
        }

        await ReadCollection.UpdateOneAsync(
            Builders<ReadFeaturedStickerSetsDocument>.Filter.Eq(p => p.Id, id),
            Builders<ReadFeaturedStickerSetsDocument>.Update
                .Set(p => p.UserId, userId)
                .AddToSetEach(p => p.ReadSetIds, stickerSetIds),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);

        return true;
    }

    /// <summary>
    /// Resolves the featured order to catalogue rows, keeping the order and silently dropping ids whose
    /// set has since been deleted.
    /// </summary>
    private async Task<List<BsonDocument>> LoadAsync(StickerSetType type, bool archived, int offset, int limit,
        CancellationToken cancellationToken)
    {
        // `Archived != true` rather than `== false`: rows seeded before this field existed do not carry it,
        // and Mongo does not match a missing field against false — that alone made the seeded custom-emoji
        // trending list read as empty.
        var filter = archived
            ? Builders<FeaturedStickerSetDocument>.Filter.Eq(p => p.Archived, true)
            : Builders<FeaturedStickerSetDocument>.Filter.Ne(p => p.Archived, true);

        var query = Collection(type)
            .Find(filter)
            .Sort(Builders<FeaturedStickerSetDocument>.Sort.Ascending(p => p.Order).Ascending(p => p.StickerSetId));

        if (offset > 0)
        {
            query = query.Skip(offset);
        }

        if (limit > 0)
        {
            query = query.Limit(limit);
        }

        var rows = await query.ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var catalogue = await stickerSetStore.FindManyAsync(rows.ConvertAll(p => p.StickerSetId), cancellationToken);

        return rows
            .Select(p => catalogue.GetValueOrDefault(p.StickerSetId))
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
    }
}
