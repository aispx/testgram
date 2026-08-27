using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Services.Emoji;

/// <summary>
/// One soundbite: the emoji it belongs to and the document holding the <c>.ogg</c> body.
/// Seeded by <c>scripts/seed_emoji_sounds.py</c>; there is no API that creates these, exactly as on
/// the official server, where the list is part of the client configuration.
/// </summary>
[BsonIgnoreExtraElements]
public class EmojiSoundDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>The emoji the sound plays for, as Telegram serves it (no U+FE0F).</summary>
    public string Emoticon { get; set; } = string.Empty;

    public long DocumentId { get; set; }

    /// <summary>Serving order; only there to keep the map stable between refreshes.</summary>
    public int Order { get; set; }
}

/// <summary>A soundbite resolved against the document read model.</summary>
/// <param name="Emoticon">The emoji key of the <c>emojies_sounds</c> map.</param>
/// <param name="DocumentId">Document id a client passes to <c>upload.getFile</c>.</param>
/// <param name="FileReference">
/// The document's <a href="https://corefork.telegram.org/api/file-references">file reference</a>,
/// handed out base64url-encoded.
/// </param>
public sealed record EmojiSound(string Emoticon, long DocumentId, byte[] FileReference);

/// <summary>
/// In-process snapshot of the <a href="https://corefork.telegram.org/api/animated-emojis#emojis-with-sounds">emoji
/// soundbites</a> advertised through <c>help.getAppConfig</c>.
/// <para>
/// The snapshot exists because <c>emojies_sounds</c> is rebuilt on every <c>help.getAppConfig</c> call -
/// the access hashes in it are per session (see <see cref="MyTelegram.Services.Services.AccessHashHelper2"/>)
/// so the entry cannot be cached with the rest of the config - and that method is answered on every
/// client start.
/// </para>
/// <para>
/// An emoji whose document row is missing is dropped rather than served: the client would download
/// nothing for it and retry on every refresh, which is exactly how the empty pickers behaved before
/// <c>account.getDefault*Emojis</c> was fixed.
/// </para>
/// </summary>
public interface IEmojiSoundStore
{
    /// <summary>Every soundbite that resolves to an existing document, in seeded order.</summary>
    Task<IReadOnlyList<EmojiSound>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class EmojiSoundStore(IMongoDatabase mongoDatabase, ILogger<EmojiSoundStore> logger)
    : IEmojiSoundStore, ISingletonDependency
{
    public const string CollectionName = "emoji_sounds";
    public const string DocumentCollectionName = "eventflow-documentreadmodel";

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile IReadOnlyList<EmojiSound> _sounds = [];
    private DateTime _loadedAt = DateTime.MinValue;

    public async Task<IReadOnlyList<EmojiSound>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken);

        return _sounds;
    }

    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _loadedAt < RefreshInterval)
        {
            return;
        }

        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            // Another request is already reloading; serving the previous snapshot for a moment longer
            // is better than queueing every getAppConfig behind one query.
            return;
        }

        try
        {
            if (DateTime.UtcNow - _loadedAt < RefreshInterval)
            {
                return;
            }

            _sounds = await LoadAsync(cancellationToken);
            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // A soundbite that cannot be loaded must not take the whole client configuration down.
            logger.LogWarning(ex, "Failed to refresh the emoji sound snapshot");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<EmojiSound>> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await mongoDatabase
            .GetCollection<EmojiSoundDocument>(CollectionName)
            .Find(Builders<EmojiSoundDocument>.Filter.Empty)
            .Sort(Builders<EmojiSoundDocument>.Sort.Ascending(p => p.Order))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var documentIds = rows
            .Where(p => p.DocumentId != 0 && !string.IsNullOrEmpty(p.Emoticon))
            .Select(p => p.DocumentId)
            .Distinct()
            .ToList();

        var references = await LoadFileReferencesAsync(documentIds, cancellationToken);

        var result = new List<EmojiSound>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.DocumentId == 0 || string.IsNullOrEmpty(row.Emoticon))
            {
                continue;
            }

            if (!references.TryGetValue(row.DocumentId, out var fileReference))
            {
                logger.LogWarning("Emoji sound {Emoticon} points at missing document {DocumentId}, dropping it",
                    row.Emoticon, row.DocumentId);
                continue;
            }

            if (seen.Add(row.Emoticon))
            {
                result.Add(new EmojiSound(row.Emoticon, row.DocumentId, fileReference));
            }
        }

        return result;
    }

    private async Task<Dictionary<long, byte[]>> LoadFileReferencesAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken)
    {
        var references = new Dictionary<long, byte[]>(documentIds.Count);
        if (documentIds.Count == 0)
        {
            return references;
        }

        var documents = await mongoDatabase
            .GetCollection<BsonDocument>(DocumentCollectionName)
            .Find(Builders<BsonDocument>.Filter.In("DocumentId",
                documentIds.Select(p => (BsonValue)new BsonInt64(p))))
            .Project(Builders<BsonDocument>.Projection
                .Include("DocumentId")
                .Include("FileReference"))
            .ToListAsync(cancellationToken);

        foreach (var document in documents)
        {
            // Binary from the server, an array of numbers from the seeders - StickerBson reads both.
            references[document.GetInt64("DocumentId")] = document.GetFileReference();
        }

        return references;
    }
}
