using MongoDB.Driver;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Backfills the inline preview (<c>photoStrippedSize</c>) for stories posted before the server
/// started generating one.
/// </summary>
/// <remarks>
/// <para>
/// The Android client draws a story's profile preview tile from the inline stripped thumbnail;
/// <c>ImageLoader.createStripedBitmap</c> returns nothing when the thumbnail list holds no
/// <c>photoStrippedSize</c>, so the tile stays blank. Clients never upload one, and until now the
/// server did not generate one either, so every existing story is missing it.
/// </para>
/// <para>
/// This reuses the same generator the upload path uses, so a backfilled story is byte-identical to a
/// freshly posted one. It is idempotent: stories that already have a preview are skipped, and a
/// story whose source bytes cannot be fetched is left alone rather than blanked.
/// </para>
/// </remarks>
public interface IStoryThumbBackfillService
{
    /// <summary>Generates missing previews and returns what happened.</summary>
    /// <param name="dryRun">When true, reports what would change without writing.</param>
    /// <param name="limit">Maximum stories to process; 0 means all.</param>
    Task<StoryThumbBackfillResult> RunAsync(bool dryRun = false, int limit = 0, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a backfill run.</summary>
public sealed record StoryThumbBackfillResult(
    int Candidates,
    int Generated,
    int Failed,
    List<string> Failures);

public class StoryThumbBackfillService(
    IMongoDatabase mongoDatabase,
    ILogger<StoryThumbBackfillService> logger)
    : IStoryThumbBackfillService, ITransientDependency
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    private readonly IMongoCollection<PhotoReadModel> _photoCollection =
        mongoDatabase.GetCollection<PhotoReadModel>("eventflow-photoreadmodel");

    private readonly IMongoCollection<DocumentReadModel> _documentCollection =
        mongoDatabase.GetCollection<DocumentReadModel>("eventflow-documentreadmodel");

    public async Task<StoryThumbBackfillResult> RunAsync(
        bool dryRun = false,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Ne(s => s.MediaFileId, 0),
            filterBuilder.Eq(s => s.Deleted, false),
            filterBuilder.Or(
                filterBuilder.Eq(s => s.StrippedThumbBytes, null),
                filterBuilder.Exists(s => s.StrippedThumbBytes, false)));

        IFindFluent<StoryDocument, StoryDocument> query =
            _storyCollection.Find(filter).SortByDescending(s => s.StoryId);
        if (limit > 0)
        {
            query = query.Limit(limit);
        }

        var stories = await query.ToListAsync(cancellationToken);

        // The per-size breakdown lives in the photo/document read models, and the generator needs a
        // size type to know which stored object to fetch.
        var photos = await LoadPhotosAsync(stories, cancellationToken);
        var documents = await LoadDocumentsAsync(stories, cancellationToken);

        var generated = 0;
        var failures = new List<string>();

        foreach (var story in stories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var media = BuildMediaForThumbnailing(story, photos, documents);
            if (media == null)
            {
                failures.Add($"story {story.StoryId}: no usable size in the read model");
                continue;
            }

            byte[]? thumb;
            try
            {
                thumb = await PaidMediaHelper.TryCreatePreviewThumbFromStoredMediaAsync(mongoDatabase, media);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Story {StoryId}: preview generation threw", story.StoryId);
                failures.Add($"story {story.StoryId}: {ex.GetType().Name}");
                continue;
            }

            if (thumb is not { Length: > 0 })
            {
                failures.Add($"story {story.StoryId}: source bytes unavailable");
                continue;
            }

            if (!dryRun)
            {
                await _storyCollection.UpdateOneAsync(
                    Builders<StoryDocument>.Filter.Eq(s => s.Id, story.Id),
                    Builders<StoryDocument>.Update.Set(s => s.StrippedThumbBytes, thumb),
                    cancellationToken: cancellationToken);
            }

            generated++;
        }

        logger.LogInformation(
            "Story preview backfill: {Candidates} candidates, {Generated} generated, {Failed} failed (dryRun={DryRun})",
            stories.Count, generated, failures.Count, dryRun);

        return new StoryThumbBackfillResult(stories.Count, generated, failures.Count, failures);
    }

    private async Task<Dictionary<long, IPhotoReadModel>> LoadPhotosAsync(
        List<StoryDocument> stories,
        CancellationToken cancellationToken)
    {
        var ids = stories
            .Where(s => s.MediaType == StoryHelper.MediaTypePhoto)
            .Select(s => s.MediaFileId)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await _photoCollection
            .Find(Builders<PhotoReadModel>.Filter.In(p => p.PhotoId, ids))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(p => p.PhotoId)
            .ToDictionary(g => g.Key, g => (IPhotoReadModel)g.First());
    }

    private async Task<Dictionary<long, IDocumentReadModel>> LoadDocumentsAsync(
        List<StoryDocument> stories,
        CancellationToken cancellationToken)
    {
        var ids = stories
            .Where(s => s.MediaType == StoryHelper.MediaTypeVideo)
            .Select(s => s.MediaFileId)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await _documentCollection
            .Find(Builders<DocumentReadModel>.Filter.In(d => d.DocumentId, ids))
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(d => d.DocumentId)
            .ToDictionary(g => g.Key, g => (IDocumentReadModel)g.First());
    }

    /// <summary>
    /// Rebuilds the minimum <c>MessageMedia</c> the generator needs: an id plus the largest size
    /// type, which together identify the stored object to downscale.
    /// </summary>
    private static IMessageMedia? BuildMediaForThumbnailing(
        StoryDocument story,
        IReadOnlyDictionary<long, IPhotoReadModel> photos,
        IReadOnlyDictionary<long, IDocumentReadModel> documents)
    {
        if (story.MediaType == StoryHelper.MediaTypePhoto)
        {
            if (!photos.TryGetValue(story.MediaFileId, out var photo))
            {
                return null;
            }

            var sizes = new TVector<IPhotoSize>();
            foreach (var size in photo.Sizes ?? [])
            {
                if (size.Type == "i")
                {
                    continue;
                }

                sizes.Add(new TPhotoSize { Type = size.Type, W = size.W, H = size.H, Size = (int)size.Size });
            }

            return sizes.Count == 0
                ? null
                : new TMessageMediaPhoto { Photo = new TPhoto { Id = story.MediaFileId, Sizes = sizes } };
        }

        if (story.MediaType == StoryHelper.MediaTypeVideo)
        {
            if (!documents.TryGetValue(story.MediaFileId, out var document))
            {
                return null;
            }

            var thumbs = new TVector<IPhotoSize>();
            foreach (var size in document.Thumbs ?? [])
            {
                if (size.Type == "i")
                {
                    continue;
                }

                thumbs.Add(new TPhotoSize { Type = size.Type, W = size.W, H = size.H, Size = (int)size.Size });
            }

            return thumbs.Count == 0
                ? null
                : new TMessageMediaDocument
                {
                    Document = new TDocument
                    {
                        Id = story.MediaFileId,
                        Attributes = new TVector<IDocumentAttribute>(),
                        Thumbs = thumbs
                    }
                };
        }

        return null;
    }
}
