using MongoDB.Driver;
using MyTelegram.Messenger.Services.Documents;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Loads GIF documents out of the document read model and turns them into TL <c>document</c>
/// objects.
///
/// <para>Everything but the search is <see cref="IDocumentReader"/>, which is shared with the other
/// surfaces that serve stored documents (saved ringtones). A GIF without <c>thumbs</c> gives the client
/// nothing to draw as the grid tile, and a GIF without <c>documentAttributeAnimated</c> is not a GIF at
/// all as far as the client is concerned — both come from the mapper that reader uses.</para>
/// </summary>
public interface IGifDocumentReader : IDocumentReader
{
    /// <summary>
    /// GIF documents stored on this server, for the local half of GIF search. Matched on the
    /// filename recorded with the document; an empty query returns the most recently created ones.
    /// </summary>
    Task<List<IDocumentReadModel>> SearchAnimatedAsync(string? query, int limit,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGifDocumentReader" />
public class GifDocumentReader(IMongoDatabase mongoDatabase, IObjectMapper objectMapper)
    : DocumentReader(mongoDatabase, objectMapper), IGifDocumentReader, ITransientDependency
{
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
}
