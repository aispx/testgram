using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Translation;

/// <summary>
/// Remembers translations so the same text is never paid for twice.
///
/// <para>This is not an optimisation. Clients re-request the messages on screen — Android translates
/// every message of a chat it is translating, in batches of twenty, and asks again whenever its cache is
/// dropped — while DeepL bills per character and the free tier is a million of them a month. Without a
/// cache, one scrolled channel is the whole monthly allowance.</para>
///
/// <para>The key is the text, not the message: the same forwarded post translated in ten chats is one
/// translation. Premium is part of the key because a non-Premium caller is answered without entities,
/// so the two answers to the same text genuinely differ.</para>
/// </summary>
public interface ITranslationCache
{
    Task<TranslatedText?> GetAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task SetAsync(string cacheKey, TranslatedText value, CancellationToken cancellationToken = default);

    /// <summary>
    /// The key for one text. Includes the entities, because the same words with different formatting
    /// translate to the same words with different formatting.
    /// </summary>
    string BuildKey(string text, IReadOnlyList<IMessageEntity>? entities, string targetLanguage,
        string? tone, bool withEntities);
}

/// <inheritdoc />
public class TranslationCache(
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TranslationCache> logger)
    : ITranslationCache, ITransientDependency
{
    public const string CollectionName = "translation_texts";

    private static int _indexInitialized;

    private IMongoCollection<BsonDocument> Collection =>
        mongoDatabase.GetCollection<BsonDocument>(CollectionName);

    public string BuildKey(string text, IReadOnlyList<IMessageEntity>? entities, string targetLanguage,
        string? tone, bool withEntities)
    {
        var builder = new StringBuilder();

        builder.Append(targetLanguage).Append('|')
            .Append(tone ?? string.Empty).Append('|')
            .Append(withEntities ? '1' : '0').Append('|')
            .Append(text);

        if (entities != null)
        {
            foreach (var entity in entities)
            {
                builder.Append('|').Append(entity.ConstructorId).Append(':')
                    .Append(entity.Offset).Append(':').Append(entity.Length);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public async Task<TranslatedText?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var document = await Collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", cacheKey))
            .FirstOrDefaultAsync(cancellationToken);

        if (document == null)
        {
            return null;
        }

        var text = document.GetValue("Text", BsonNull.Value) is { BsonType: BsonType.String } value
            ? value.AsString
            : string.Empty;

        return new TranslatedText(text, ReadEntities(document));
    }

    public async Task SetAsync(string cacheKey, TranslatedText value,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        var update = Builders<BsonDocument>.Update
            .Set("Text", value.Text)
            .Set("Date", DateTime.UtcNow);

        // A TL blob rather than a mapped subdocument: entity types differ by constructor and the driver
        // has no discriminator for them, while the schema already round-trips them faithfully.
        update = value.Entities.Count > 0
            ? update.Set("Entities", new BsonBinaryData(value.Entities.ToBytes()!))
            : update.Unset("Entities");

        await Collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", cacheKey),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// The cache expires on its own. A translation is only worth keeping while a client might still ask
    /// for the same text, and an unbounded collection of every message ever translated is not something
    /// a deployment should have to notice.
    /// </summary>
    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _indexInitialized, 1) == 1)
        {
            return;
        }

        try
        {
            await Collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("Date"),
                    new CreateIndexOptions
                    {
                        Name = "translation_texts_ttl",
                        ExpireAfter = TimeSpan.FromDays(
                            Math.Max(1, options.CurrentValue.Translation.CacheDays))
                    }),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The translation cache index could not be created");
        }
    }

    private TVector<IMessageEntity> ReadEntities(BsonDocument document)
    {
        if (document.GetValue("Entities", BsonNull.Value) is not { BsonType: BsonType.Binary } binary)
        {
            return [];
        }

        try
        {
            return binary.AsBsonBinaryData.Bytes.ToTObject<TVector<IMessageEntity>>() ?? [];
        }
        catch (Exception ex)
        {
            // A blob written by an older layer. Serving the text without entities is right; throwing
            // here would fail a translation that is otherwise perfectly good.
            logger.LogWarning(ex, "Could not read the cached entities of a translation; serving plain text");

            return [];
        }
    }
}
