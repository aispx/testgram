using MongoDB.Driver;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>Media of a story, resolved to the ids the client needs to download it.</summary>
public sealed class StoryMediaInfo
{
    /// <summary>1 = photo, 2 = document/video.</summary>
    public int MediaType { get; init; }

    public long FileId { get; init; }
    public long AccessHash { get; init; }
    public byte[] FileReference { get; init; } = [];
    public int DcId { get; init; }
    public long Size { get; init; }
    public string? MimeType { get; init; }
    public int? VideoWidth { get; init; }
    public int? VideoHeight { get; init; }
    public int? VideoDuration { get; init; }

    /// <summary>
    /// Inline low-resolution preview (<c>photoStrippedSize</c>) for the story's media.
    /// </summary>
    /// <remarks>
    /// The Android client draws the profile preview tile from this via
    /// <c>ImageLoader.createStripedBitmap</c>, which returns nothing when the thumbnail list holds no
    /// <c>photoStrippedSize</c> — so without it the tile stays blank until (and unless) the full file
    /// arrives. Clients do not upload one, so the server generates it.
    /// </remarks>
    public byte[]? StrippedThumbBytes { get; init; }
}

public interface IStoryMediaService
{
    /// <summary>
    /// Persists story media on the file server and returns the resolved ids.
    /// Throws <c>MEDIA_EMPTY</c> / <c>MEDIA_TYPE_INVALID</c> rather than a bare exception on failure.
    /// </summary>
    Task<StoryMediaInfo> SaveStoryMediaAsync(IInputMedia media);
}

/// <summary>
/// Turns a story's <c>InputMedia</c> into stored media.
/// <para>
/// Both stories.sendStory and stories.editStory need the media <em>document</em> id, access hash, DC and
/// file reference — not the upload's <c>InputFile.Id</c>, which is only meaningful during the upload and
/// cannot be used to download the file afterwards.
/// </para>
/// </summary>
public class StoryMediaService(
    IMediaHelper mediaHelper,
    IMongoDatabase mongoDatabase,
    ILogger<StoryMediaService> logger) : IStoryMediaService, ITransientDependency
{
    public async Task<StoryMediaInfo> SaveStoryMediaAsync(IInputMedia media)
    {
        var savedMedia = await mediaHelper.SaveMediaAsync(media);
        if (savedMedia == null)
        {
            RpcErrors.RpcErrors400.MediaEmpty.ThrowRpcError();
        }

        // Derived from the stored media rather than the upload, so it works for both photo and video
        // and does not depend on the client having sent a thumbnail.
        var strippedThumb = await TryCreateStrippedThumbAsync(savedMedia!);

        switch (savedMedia)
        {
            case TMessageMediaPhoto { Photo: TPhoto photo }:
                return new StoryMediaInfo
                {
                    MediaType = 1,
                    FileId = photo.Id,
                    AccessHash = photo.AccessHash,
                    FileReference = photo.FileReference.Length > 0 ? photo.FileReference.ToArray() : [],
                    DcId = photo.DcId,
                    Size = LargestPhotoSize(photo),
                    StrippedThumbBytes = strippedThumb
                };

            case TMessageMediaDocument { Document: TDocument document }:
                var videoAttribute = document.Attributes?
                    .OfType<TDocumentAttributeVideo>()
                    .FirstOrDefault();

                return new StoryMediaInfo
                {
                    MediaType = 2,
                    FileId = document.Id,
                    AccessHash = document.AccessHash,
                    FileReference = document.FileReference.Length > 0 ? document.FileReference.ToArray() : [],
                    DcId = document.DcId,
                    Size = document.Size,
                    MimeType = document.MimeType,
                    VideoWidth = videoAttribute?.W,
                    VideoHeight = videoAttribute?.H,
                    VideoDuration = videoAttribute != null ? (int)videoAttribute.Duration : null,
                    StrippedThumbBytes = document.Thumbs?
                        .OfType<TPhotoStrippedSize>()
                        .FirstOrDefault()?.Bytes.ToArray() ?? strippedThumb
                };

            default:
                RpcErrors.RpcErrors400.MediaTypeInvalid.ThrowRpcError();
                return null!;
        }
    }

    /// <summary>
    /// Builds the inline preview from the stored media, tolerating failure.
    /// </summary>
    /// <remarks>
    /// A story must still post if the preview cannot be produced — a missing tile is far better than
    /// a rejected upload — so every failure is logged and swallowed.
    /// </remarks>
    private async Task<byte[]?> TryCreateStrippedThumbAsync(IMessageMedia savedMedia)
    {
        try
        {
            return await PaidMediaHelper.TryCreatePreviewThumbFromStoredMediaAsync(mongoDatabase, savedMedia);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not build the inline story preview; the story will have no preview tile");
            return null;
        }
    }

    private static long LargestPhotoSize(TPhoto photo)
    {
        if (photo.Sizes == null)
        {
            return 0;
        }

        long largest = 0;
        foreach (var size in photo.Sizes)
        {
            var candidate = size switch
            {
                TPhotoSize photoSize => photoSize.Size,
                TPhotoSizeProgressive progressive => progressive.Sizes?.Count > 0 ? progressive.Sizes.Max() : 0,
                _ => 0
            };

            if (candidate > largest)
            {
                largest = candidate;
            }
        }

        return largest;
    }
}
