using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Maps an uploaded <c>image/gif</c> document to the MPEG4 document the server produced from it.
/// See https://corefork.telegram.org/api/gifs#uploading-gifs
/// </summary>
[BsonIgnoreExtraElements]
public class GifMp4ConversionDocument
{
    /// <summary>Document id of the original (non-MPEG4) animation.</summary>
    [BsonId]
    public long SourceDocumentId { get; set; }

    /// <summary>Document id of the converted, silent MPEG4.</summary>
    public long Mp4DocumentId { get; set; }

    public int Date { get; set; }
}

/// <summary>
/// Remembers GIF → MPEG4 conversions, so the same upload is transcoded once and
/// <c>messages.saveGif</c> called with the original document id can still resolve to the MPEG4
/// twin that clients actually accept.
/// </summary>
public interface IGifMp4ConversionStore
{
    Task<long?> GetMp4DocumentIdAsync(long sourceDocumentId, CancellationToken cancellationToken = default);

    Task SetAsync(long sourceDocumentId, long mp4DocumentId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class GifMp4ConversionStore(IMongoDatabase mongoDatabase) : IGifMp4ConversionStore, ITransientDependency
{
    public const string CollectionName = "gif_mp4_conversions";

    private IMongoCollection<GifMp4ConversionDocument> Collection =>
        mongoDatabase.GetCollection<GifMp4ConversionDocument>(CollectionName);

    public async Task<long?> GetMp4DocumentIdAsync(long sourceDocumentId,
        CancellationToken cancellationToken = default)
    {
        var document = await Collection
            .Find(Builders<GifMp4ConversionDocument>.Filter.Eq(p => p.SourceDocumentId, sourceDocumentId))
            .FirstOrDefaultAsync(cancellationToken);

        return document?.Mp4DocumentId;
    }

    public Task SetAsync(long sourceDocumentId, long mp4DocumentId,
        CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(
            Builders<GifMp4ConversionDocument>.Filter.Eq(p => p.SourceDocumentId, sourceDocumentId),
            Builders<GifMp4ConversionDocument>.Update
                .Set(p => p.Mp4DocumentId, mp4DocumentId)
                .Set(p => p.Date, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }
}
