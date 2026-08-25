using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Loads GIF documents out of the document read model and turns them into TL <c>document</c>
/// objects.
///
/// <para>The mapping goes through <c>DocumentMapper</c> (via <see cref="IObjectMapper"/>) rather
/// than a hand-rolled converter, because that mapper is the only one that fills <c>thumbs</c>,
/// <c>video_thumbs</c> and the decoded attributes. A GIF without <c>thumbs</c> gives the client
/// nothing to draw as the grid tile, and a GIF without <c>documentAttributeAnimated</c> is not a
/// GIF at all as far as the client is concerned.</para>
/// </summary>
public interface IGifDocumentReader
{
    /// <summary>The read models for the given ids, in one round trip, keyed by document id.</summary>
    Task<Dictionary<long, IDocumentReadModel>> GetAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default);

    /// <summary>The read model for a single document, or null when it does not exist.</summary>
    Task<IDocumentReadModel?> GetAsync(long documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GIF documents stored on this server, for the local half of GIF search. Matched on the
    /// filename recorded with the document; an empty query returns the most recently created ones.
    /// </summary>
    Task<List<IDocumentReadModel>> SearchAnimatedAsync(string? query, int limit,
        CancellationToken cancellationToken = default);

    TDocument Map(IDocumentReadModel document);
}

/// <inheritdoc />
public class GifDocumentReader(IMongoDatabase mongoDatabase, IObjectMapper objectMapper)
    : IGifDocumentReader, ITransientDependency
{
    private const string CollectionName = "eventflow-documentreadmodel";

    // Read the collection directly: GetDocumentsByIdListQuery is declared but has no registered
    // handler, so dispatching it would throw at runtime (same reason StoryResponseBuilder does this).
    private IMongoCollection<DocumentReadModel> Collection =>
        mongoDatabase.GetCollection<DocumentReadModel>(CollectionName);

    public async Task<Dictionary<long, IDocumentReadModel>> GetAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var documents = await Collection
            .Find(Builders<DocumentReadModel>.Filter.In(p => p.DocumentId, documentIds))
            .ToListAsync(cancellationToken);

        return documents
            .GroupBy(p => p.DocumentId)
            .ToDictionary(p => p.Key, p => (IDocumentReadModel)p.First());
    }

    public async Task<IDocumentReadModel?> GetAsync(long documentId, CancellationToken cancellationToken = default)
    {
        return await Collection
            .Find(Builders<DocumentReadModel>.Filter.Eq(p => p.DocumentId, documentId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<IDocumentReadModel>> SearchAnimatedAsync(string? query, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var builder = Builders<DocumentReadModel>.Filter;
        var filter = builder.Eq(p => p.MimeType, GifDocumentHelper.Mp4MimeType);

        if (!string.IsNullOrWhiteSpace(query))
        {
            // Escaped: a query is arbitrary user input and must not be able to inject a pattern.
            filter &= builder.Regex(p => p.Name,
                new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Trim()), "i"));
        }

        // The mime filter is indexable, the animated-attribute check is not, so over-fetch a little
        // and filter in memory rather than asking Mongo to scan attribute arrays.
        var candidates = await Collection
            .Find(filter)
            .Sort(Builders<DocumentReadModel>.Sort.Descending(p => p.Date))
            .Limit(limit * 4)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(GifDocumentHelper.IsAnimatedMp4)
            .Take(limit)
            .Cast<IDocumentReadModel>()
            .ToList();
    }

    public TDocument Map(IDocumentReadModel document)
    {
        var mapped = objectMapper.Map<IDocumentReadModel, TDocument>(document);

        // DocumentMapper leaves these null when the read model has neither; the client throws on a
        // null vector.
        mapped.Thumbs ??= new TVector<IPhotoSize>();
        mapped.VideoThumbs ??= new TVector<IVideoSize>();
        mapped.Attributes ??= new TVector<IDocumentAttribute>();

        return mapped;
    }
}
