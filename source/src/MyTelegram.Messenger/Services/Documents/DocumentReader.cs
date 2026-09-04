using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Documents;

/// <summary>
/// Reads rows out of the document read model and turns them into TL <c>document</c> objects.
///
/// <para>The mapping goes through <c>DocumentMapper</c> (via <see cref="IObjectMapper"/>) rather than a
/// hand-rolled converter, because that mapper is the only one that fills <c>thumbs</c>,
/// <c>video_thumbs</c>, decodes the stored attributes — including the stickerset reference inside one —
/// and mints a fresh <c>file_reference</c>. Every hand-rolled copy of this that has existed in the
/// repository decoded two attribute types, guessed <c>dc_id</c> and handed out media no client could
/// download or refresh.</para>
/// See https://corefork.telegram.org/api/file-references
/// </summary>
public interface IDocumentReader
{
    /// <summary>The read models for the given ids, in one round trip, keyed by document id.</summary>
    Task<Dictionary<long, IDocumentReadModel>> GetAsync(IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default);

    /// <summary>The read model for a single document, or null when it does not exist.</summary>
    Task<IDocumentReadModel?> GetAsync(long documentId, CancellationToken cancellationToken = default);

    TDocument Map(IDocumentReadModel document);
}

/// <inheritdoc />
public class DocumentReader(IMongoDatabase mongoDatabase, IObjectMapper objectMapper)
    : IDocumentReader, ITransientDependency
{
    protected const string CollectionName = "eventflow-documentreadmodel";

    // Read the collection directly: GetDocumentsByIdListQuery is declared but has no registered
    // handler, so dispatching it would throw at runtime (same reason StoryResponseBuilder does this).
    protected IMongoCollection<DocumentReadModel> Collection =>
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
