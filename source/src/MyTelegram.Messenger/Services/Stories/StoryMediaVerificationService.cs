using MongoDB.Driver;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Flags stories whose stored media is not a usable image.
/// </summary>
/// <remarks>
/// <para>
/// Some stories in this deployment were seeded with 1x1-pixel placeholder objects instead of real
/// uploads: the read model declares, say, a 450x800 size of 43283 bytes, while the object behind it
/// is a 284-byte single-pixel JPEG. The client believes the declaration, downloads the placeholder
/// and stretches it over the whole tile, which shows as a flat block of colour — the "pixels" a
/// viewer sees instead of a picture.
/// </para>
/// <para>
/// Detection reuses the same fetch-and-decode path the preview generator uses, so a story is only
/// condemned when its bytes genuinely cannot produce an image. Anything merely unreachable is left
/// alone: a transient storage error must not mark good media as broken.
/// </para>
/// </remarks>
public interface IStoryMediaVerificationService
{
    /// <summary>Verifies stored media and flags the stories that cannot be rendered.</summary>
    /// <param name="dryRun">When true, reports what would change without writing.</param>
    /// <param name="limit">Maximum stories to check; 0 means all.</param>
    Task<StoryMediaVerificationResult> RunAsync(
        bool dryRun = false,
        int limit = 0,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a verification run.</summary>
public sealed record StoryMediaVerificationResult(
    int Checked,
    int Unusable,
    int Usable,
    List<int> UnusableStoryIds);

public class StoryMediaVerificationService(
    IMongoDatabase mongoDatabase,
    ILogger<StoryMediaVerificationService> logger)
    : IStoryMediaVerificationService, ITransientDependency
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    private readonly IMongoCollection<PhotoReadModel> _photoCollection =
        mongoDatabase.GetCollection<PhotoReadModel>("eventflow-photoreadmodel");

    public async Task<StoryMediaVerificationResult> RunAsync(
        bool dryRun = false,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<StoryDocument>.Filter;
        var filter = filterBuilder.And(
            filterBuilder.Ne(s => s.MediaFileId, 0),
            filterBuilder.Eq(s => s.MediaType, StoryHelper.MediaTypePhoto),
            filterBuilder.Eq(s => s.Deleted, false));

        IFindFluent<StoryDocument, StoryDocument> query =
            _storyCollection.Find(filter).SortByDescending(s => s.StoryId);
        if (limit > 0)
        {
            query = query.Limit(limit);
        }

        var stories = await query.ToListAsync(cancellationToken);
        var photos = await LoadPhotosAsync(stories, cancellationToken);

        var unusableIds = new List<int>();
        var usable = 0;

        // One media file can back several stories; decide per file and reuse the verdict.
        var verdicts = new Dictionary<long, bool>();

        foreach (var story in stories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!verdicts.TryGetValue(story.MediaFileId, out var isUsable))
            {
                isUsable = await IsUsablePhotoAsync(story, photos);
                verdicts[story.MediaFileId] = isUsable;
            }

            if (isUsable)
            {
                usable++;

                // Clear a stale flag if the media was replaced with something real.
                if (story.MediaUnusable && !dryRun)
                {
                    await SetUnusableAsync(story, false, cancellationToken);
                }

                continue;
            }

            unusableIds.Add(story.StoryId);

            if (!story.MediaUnusable && !dryRun)
            {
                await SetUnusableAsync(story, true, cancellationToken);
            }
        }

        logger.LogInformation(
            "Story media verification: {Checked} checked, {Usable} usable, {Unusable} unusable (dryRun={DryRun})",
            stories.Count, usable, unusableIds.Count, dryRun);

        return new StoryMediaVerificationResult(stories.Count, unusableIds.Count, usable, unusableIds);
    }

    private Task SetUnusableAsync(StoryDocument story, bool unusable, CancellationToken cancellationToken) =>
        _storyCollection.UpdateOneAsync(
            Builders<StoryDocument>.Filter.Eq(s => s.Id, story.Id),
            Builders<StoryDocument>.Update.Set(s => s.MediaUnusable, unusable),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Decides whether a story's largest declared size is backed by a real object.
    /// </summary>
    /// <remarks>
    /// Compares the declared byte length with the object actually in storage. A genuine upload
    /// matches exactly; a placeholder is a few hundred bytes behind a much larger declaration. This
    /// deliberately avoids decrypting: placeholders were written unencrypted, so decrypting one
    /// yields garbage that is indistinguishable from a transient read failure.
    /// <para>
    /// An unfetchable object returns <c>true</c> — the media gets the benefit of the doubt, because
    /// wrongly condemning a good story is far worse than leaving a broken one on the profile.
    /// </para>
    /// </remarks>
    private async Task<bool> IsUsablePhotoAsync(
        StoryDocument story,
        IReadOnlyDictionary<long, IPhotoReadModel> photos)
    {
        if (!photos.TryGetValue(story.MediaFileId, out var photo) || photo.Sizes is not { Count: > 0 })
        {
            return true;
        }

        var largest = photo.Sizes
            .Where(s => s.Type != "i")
            .OrderByDescending(s => (long)s.W * s.H)
            .FirstOrDefault();

        if (largest is not { Size: > 0 })
        {
            return true;
        }

        long? actual;
        try
        {
            actual = await PaidMediaHelper.TryGetStoredObjectLengthAsync(story.MediaFileId, largest.Type);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Story {StoryId}: could not measure stored media", story.StoryId);
            return true;
        }

        if (actual == null)
        {
            return true;
        }

        if (actual.Value == largest.Size)
        {
            return true;
        }

        // A shortfall this large cannot be a rounding or encoding difference; the declaration does
        // not describe the object behind it.
        var usable = actual.Value >= largest.Size / 2;
        if (!usable)
        {
            logger.LogInformation(
                "Story {StoryId}: stored media is {Actual} bytes against a declared {Declared} — treating as unusable",
                story.StoryId, actual.Value, largest.Size);
        }

        return usable;
    }

    private async Task<Dictionary<long, IPhotoReadModel>> LoadPhotosAsync(
        List<StoryDocument> stories,
        CancellationToken cancellationToken)
    {
        var ids = stories.Select(s => s.MediaFileId).Distinct().ToList();
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
}
