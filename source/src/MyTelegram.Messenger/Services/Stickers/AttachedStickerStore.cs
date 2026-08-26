using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Which stickersets were used on one photo or video.
/// </summary>
[BsonIgnoreExtraElements]
public class AttachedStickersDocument
{
    /// <summary><c>photo:{id}</c> or <c>document:{id}</c> — the two id spaces are separate.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public List<long> StickerSetIds { get; set; } = [];

    public int Date { get; set; }

    public static string MakePhotoId(long photoId) => $"photo:{photoId}";

    public static string MakeDocumentId(long documentId) => $"document:{documentId}";
}

/// <summary>
/// Records and reads the stickersets attached to a photo or video.
///
/// <para>The sets come from <c>inputMediaUploadedPhoto.stickers</c> /
/// <c>inputMediaUploadedDocument.stickers</c> at send time: the client bakes the stickers into the image it
/// uploads and separately tells the server which ones it used, so that other users can find the packs
/// through <c>messages.getAttachedStickers</c>. Nothing read that field before, which is why the method had
/// no data to answer with.</para>
/// See https://corefork.telegram.org/api/stickers#attached-stickers
/// </summary>
public interface IAttachedStickerStore
{
    Task SaveAsync(string id, IReadOnlyCollection<long> stickerSetIds,
        CancellationToken cancellationToken = default);

    Task<List<long>> GetAsync(string id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class AttachedStickerStore(IMongoDatabase mongoDatabase) : IAttachedStickerStore, ITransientDependency
{
    public const string CollectionName = "attached_stickers";

    private IMongoCollection<AttachedStickersDocument> Collection =>
        mongoDatabase.GetCollection<AttachedStickersDocument>(CollectionName);

    public Task SaveAsync(string id, IReadOnlyCollection<long> stickerSetIds,
        CancellationToken cancellationToken = default)
    {
        if (stickerSetIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Collection.UpdateOneAsync(
            Builders<AttachedStickersDocument>.Filter.Eq(p => p.Id, id),
            Builders<AttachedStickersDocument>.Update
                .Set(p => p.StickerSetIds, stickerSetIds.Distinct().ToList())
                .Set(p => p.Date, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<List<long>> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var row = await Collection
            .Find(Builders<AttachedStickersDocument>.Filter.Eq(p => p.Id, id))
            .FirstOrDefaultAsync(cancellationToken);

        return row?.StickerSetIds ?? [];
    }
}
