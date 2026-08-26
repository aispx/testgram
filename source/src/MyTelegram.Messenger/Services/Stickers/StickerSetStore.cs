using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Dice;

namespace MyTelegram.Messenger.Services.Stickers;

/// <inheritdoc />
public class StickerSetStore(IMongoDatabase mongoDatabase) : IStickerSetStore, ITransientDependency
{
    public const string CollectionName = "eventflow-stickersetreadmodel";

    /// <summary>
    /// The parameterless <c>InputStickerSet</c> constructors, each naming one specific set. Keep this
    /// in sync with the schema: a constructor missing here resolves to nothing, and the client quietly
    /// falls back to static system emoji.
    /// </summary>
    private static readonly Dictionary<Type, string> SpecialShortNames = new()
    {
        [typeof(TInputStickerSetAnimatedEmoji)] = "AnimatedEmojies",
        [typeof(TInputStickerSetAnimatedEmojiAnimations)] = "EmojiAnimations",
        [typeof(TInputStickerSetPremiumGifts)] = "GiftsPremium",
        [typeof(TInputStickerSetEmojiGenericAnimations)] = "EmojiGenericAnimations",
        [typeof(TInputStickerSetEmojiDefaultStatuses)] = "StatusPack",
        [typeof(TInputStickerSetEmojiDefaultTopicIcons)] = "Topics",
        [typeof(TInputStickerSetEmojiChannelDefaultStatuses)] = "StatusPack",
        [typeof(TInputStickerSetTonGifts)] = "GiftsTons"
    };

    private IMongoCollection<BsonDocument> Collection => mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public async Task<StickerSetLookup> FindAsync(IInputStickerSet? inputStickerSet,
        CancellationToken cancellationToken = default)
    {
        switch (inputStickerSet)
        {
            case TInputStickerSetID byId:
                return new StickerSetLookup(await FindByIdAsync(byId.Id, cancellationToken));

            case TInputStickerSetShortName byShortName:
                return new StickerSetLookup(await FindByShortNameAsync(byShortName.ShortName, cancellationToken));

            case TInputStickerSetDice dice:
            {
                // Which set backs each dice emoji comes from the one dice table, so sending and drawing
                // cannot disagree about what a dice is. See https://corefork.telegram.org/api/dice
                var shortName = DiceEmojiHelper.GetShortName(dice.Emoticon);
                var set = shortName == null ? null : await FindByShortNameAsync(shortName, cancellationToken);

                return new StickerSetLookup(set, dice.Emoticon);
            }

            case null:
            case TInputStickerSetEmpty:
                return default;

            default:
            {
                var shortName = SpecialShortNames.GetValueOrDefault(inputStickerSet.GetType());
                var set = shortName == null ? null : await FindByShortNameAsync(shortName, cancellationToken);

                return new StickerSetLookup(set);
            }
        }
    }

    public Task<BsonDocument?> FindByIdAsync(long stickerSetId, CancellationToken cancellationToken = default)
    {
        return Collection
            .Find(Builders<BsonDocument>.Filter.Eq("StickerSetId", stickerSetId))
            .FirstOrDefaultAsync(cancellationToken)!;
    }

    public async Task<BsonDocument?> FindByShortNameAsync(string shortName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(shortName))
        {
            return null;
        }

        // Seeded rows carry the name in Slug, rows created here in ShortName, and most carry both.
        // Case-insensitive because t.me/addstickers links are not case-normalised by clients.
        var pattern = new BsonRegularExpression($"^{System.Text.RegularExpressions.Regex.Escape(shortName)}$", "i");

        return await Collection
            .Find(Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("ShortName", pattern),
                Builders<BsonDocument>.Filter.Regex("Slug", pattern)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<BsonDocument?> FindByDocumentIdAsync(long documentId,
        CancellationToken cancellationToken = default)
    {
        return Collection
            .Find(Builders<BsonDocument>.Filter.AnyEq("DocumentIds", documentId))
            .FirstOrDefaultAsync(cancellationToken)!;
    }

    public async Task<Dictionary<long, BsonDocument>> FindManyAsync(IReadOnlyCollection<long> stickerSetIds,
        CancellationToken cancellationToken = default)
    {
        if (stickerSetIds.Count == 0)
        {
            return [];
        }

        var rows = await Collection
            .Find(Builders<BsonDocument>.Filter.In("StickerSetId",
                stickerSetIds.Select(p => (BsonValue)new BsonInt64(p))))
            .ToListAsync(cancellationToken);

        var result = new Dictionary<long, BsonDocument>(rows.Count);
        foreach (var row in rows)
        {
            result[row.GetInt64("StickerSetId")] = row;
        }

        return result;
    }

    public async Task<List<BsonDocument>> FindOfficialAsync(StickerSetType type, int limit,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Official", true),
            TypeFilter(type));

        return await Collection
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("Count").Ascending("StickerSetId"))
            .Limit(limit > 0 ? limit : 20)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ShortNameExistsAsync(string shortName, CancellationToken cancellationToken = default)
    {
        return await FindByShortNameAsync(shortName, cancellationToken) != null;
    }

    public async Task<List<BsonDocument>> FindByCreatorAsync(long creatorUserId, long offsetId, int limit,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("CreatorUserId", creatorUserId);
        if (offsetId > 0)
        {
            filter = Builders<BsonDocument>.Filter.And(filter,
                Builders<BsonDocument>.Filter.Lt("StickerSetId", offsetId));
        }

        return await Collection
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("StickerSetId"))
            .Limit(limit > 0 ? limit : 100)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountByCreatorAsync(long creatorUserId, CancellationToken cancellationToken = default)
    {
        var count = await Collection.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", creatorUserId),
            cancellationToken: cancellationToken);

        return (int)count;
    }

    public Task ReplaceAsync(BsonDocument stickerSetDocument, CancellationToken cancellationToken = default)
    {
        return Collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("StickerSetId", stickerSetDocument.GetInt64("StickerSetId")),
            stickerSetDocument,
            cancellationToken: cancellationToken);
    }

    public Task InsertAsync(BsonDocument stickerSetDocument, CancellationToken cancellationToken = default)
    {
        return Collection.InsertOneAsync(stickerSetDocument, cancellationToken: cancellationToken);
    }

    public Task DeleteAsync(long stickerSetId, CancellationToken cancellationToken = default)
    {
        return Collection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("StickerSetId", stickerSetId),
            cancellationToken);
    }

    public StickerSetType GetStickerSetType(BsonDocument stickerSetDocument)
    {
        if (stickerSetDocument.GetBool("Emojis"))
        {
            return StickerSetType.CustomEmoji;
        }

        return stickerSetDocument.GetBool("Masks") ? StickerSetType.Mask : StickerSetType.Regular;
    }

    /// <summary>
    /// Matches the catalogue on the two flags rather than on a stored type, and treats a missing flag
    /// as false — most seeded rows omit <c>Masks</c> entirely.
    /// </summary>
    private static FilterDefinition<BsonDocument> TypeFilter(StickerSetType type)
    {
        return type switch
        {
            StickerSetType.CustomEmoji => Builders<BsonDocument>.Filter.Eq("Emojis", true),
            StickerSetType.Mask => Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Ne("Emojis", true),
                Builders<BsonDocument>.Filter.Eq("Masks", true)),
            _ => Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Ne("Emojis", true),
                Builders<BsonDocument>.Filter.Ne("Masks", true))
        };
    }
}
