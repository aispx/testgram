using Google.Protobuf;
using MyTelegram.GrpcService;

namespace MyTelegram.Messenger.Services.VideoProcessing;

/// <summary>
/// Server side video processing.
/// </summary>
/// <remarks>
/// "Sending even non-scheduled videos to big channels will automatically trigger server-side
/// processing (i.e. to generate alternative qualities, that will be contained in the final
/// messageMediaDocument.alt_document)."
/// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
/// </remarks>
public interface IVideoProcessingService
{
    /// <summary>
    /// True when this media must be converted before the message is delivered.
    /// </summary>
    Task<bool> ShouldProcessAsync(IMessageMedia? media, Peer toPeer);

    /// <summary>
    /// Seconds the conversion is expected to take; the queued message is dated with it, as the
    /// documentation asks for the "estimated conversion date".
    /// </summary>
    int EstimateConversionSeconds(IMessageMedia? media);

    /// <summary>
    /// Builds the alternative qualities of a video and returns them as ready to send documents.
    /// An empty list means the video is to be delivered as it was uploaded.
    /// </summary>
    Task<List<IDocument>> CreateAltDocumentsAsync(TDocument source, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class VideoProcessingService(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IChannelAppService channelAppService,
    IStoredFileStorage storedFileStorage,
    IVideoTranscoder videoTranscoder,
    IFfmpegLocator ffmpegLocator,
    ILogger<VideoProcessingService> logger)
    : IVideoProcessingService, ITransientDependency
{
    private VideoProcessingConfig Config => options.CurrentValue.VideoProcessing;

    public async Task<bool> ShouldProcessAsync(IMessageMedia? media, Peer toPeer)
    {
        // No ffmpeg on this host (e.g. a Windows or macOS dev box without it installed) means the video
        // could never be converted, so it must go out immediately instead of parking in the queue.
        if (!Config.Enabled || Config.Heights.Count == 0 || toPeer.PeerType != PeerType.Channel ||
            !ffmpegLocator.IsAvailable)
        {
            return false;
        }

        if (GetVideo(media) is not { } video)
        {
            return false;
        }

        var (document, attribute) = video;
        if (document.Size > Config.MaxSourceSizeBytes || attribute.Duration > Config.MaxDurationSeconds)
        {
            return false;
        }

        // Nothing to gain when even the smallest rung is not smaller than the upload itself.
        if (attribute.H <= Config.Heights.Min())
        {
            return false;
        }

        var channelReadModel = await channelAppService.GetAsync(toPeer.PeerId);
        return channelReadModel is { Broadcast: true } &&
               (channelReadModel.ParticipantsCount ?? 0) >= Config.MinChannelParticipants;
    }

    public int EstimateConversionSeconds(IMessageMedia? media)
    {
        var duration = GetVideo(media)?.Attribute.Duration ?? 0;
        var rungs = Math.Max(1, Config.Heights.Count);
        var estimate = (int)Math.Ceiling(duration * Config.EstimateSecondsPerSecond * rungs);

        return Math.Max(Config.MinEstimateSeconds, estimate);
    }

    public async Task<List<IDocument>> CreateAltDocumentsAsync(TDocument source,
        CancellationToken cancellationToken = default)
    {
        var altDocuments = new List<IDocument>();
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"video-processing-{source.Id}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var sourcePath = Path.Combine(workingDirectory, "source");
            if (!await storedFileStorage.DownloadToFileAsync(source.Id, sourcePath, cancellationToken))
            {
                return altDocuments;
            }

            var info = await videoTranscoder.ProbeAsync(sourcePath, cancellationToken);
            if (info == null)
            {
                logger.LogWarning("Video {DocumentId} could not be probed, it is sent unprocessed", source.Id);
                return altDocuments;
            }

            foreach (var height in Config.Heights.Where(p => p < info.Height).OrderBy(p => p))
            {
                var renditionPath = Path.Combine(workingDirectory, $"{height}p.mp4");
                if (!await videoTranscoder.TranscodeAsync(sourcePath, renditionPath, height, cancellationToken))
                {
                    continue;
                }

                var rendition = await videoTranscoder.ProbeAsync(renditionPath, cancellationToken);
                if (rendition == null)
                {
                    continue;
                }

                var document = await PublishRenditionAsync(renditionPath, rendition, source, cancellationToken);
                if (document != null)
                {
                    altDocuments.Add(document);
                }
            }

            logger.LogInformation("Video {DocumentId}: produced {Count} alternative qualities", source.Id,
                altDocuments.Count);

            return altDocuments;
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not clean up {Directory}", workingDirectory);
            }
        }
    }

    /// <summary>
    /// Stores one rendition and registers it as a document, so clients can download it like any other
    /// file: the body goes into the object store, the metadata through the file server.
    /// </summary>
    private async Task<IDocument?> PublishRenditionAsync(string path, VideoInfo info, TDocument source,
        CancellationToken cancellationToken)
    {
        var documentId = GenerateId();
        var accessHash = GenerateId();
        var fileReference = new byte[16];
        Random.Shared.NextBytes(fileReference);
        var size = new FileInfo(path).Length;

        await storedFileStorage.UploadFileAsync(documentId, path, cancellationToken);

        var fileName = $"{info.Height}p.mp4";
        var client = GrpcClientFactory.CreateMediaServiceClient(options.CurrentValue.FileServerGrpcServiceUrl);
        var response = await client.CreateDocumentAsync(new CreateDocumentRequest
        {
            StickerId = documentId,
            AccessHash = accessHash,
            MimeType = "video/mp4",
            Size = (int)size,
            ThumbSize = string.Empty,
            Emoji = string.Empty,
            IsAnimated = false,
            Thumb = string.Empty,
            VideoThumb = string.Empty,
            FileReference = ByteString.CopyFrom(fileReference),
            AttributeFileName = fileName,
            StickerType = 0
        }, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            logger.LogWarning("The file server refused to register the {Height}p rendition of {DocumentId}",
                info.Height, source.Id);
            return null;
        }

        // The attributes the client reads are the ones carried by the message, so the rendition
        // describes itself properly even though the file server registers it through its sticker path.
        return new TDocument
        {
            Id = documentId,
            AccessHash = accessHash,
            FileReference = fileReference,
            Date = DateTime.UtcNow.ToTimestamp(),
            MimeType = "video/mp4",
            Size = size,
            DcId = source.DcId,
            Attributes = new TVector<IDocumentAttribute>(
                new TDocumentAttributeVideo
                {
                    Duration = info.DurationSeconds,
                    W = info.Width,
                    H = info.Height,
                    SupportsStreaming = true,
                    VideoCodec = info.VideoCodec
                },
                new TDocumentAttributeFilename { FileName = fileName })
        };
    }

    private static (TDocument Document, TDocumentAttributeVideo Attribute)? GetVideo(IMessageMedia? media)
    {
        if (media is not TMessageMediaDocument { Document: TDocument document })
        {
            return null;
        }

        if (!document.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var attribute = document.Attributes.OfType<TDocumentAttributeVideo>().FirstOrDefault();

        // Round video messages and animations are never converted: they are already tiny and the
        // clients play them in their own way.
        if (attribute == null || attribute.RoundMessage ||
            document.Attributes.OfType<TDocumentAttributeAnimated>().Any())
        {
            return null;
        }

        return (document, attribute);
    }

    private static long GenerateId()
    {
        var bytes = new byte[8];
        Random.Shared.NextBytes(bytes);
        bytes[0] &= 0x7F;

        return BitConverter.ToInt64(bytes, 0) & 0x7FFFFFFFFFFFFFFF;
    }
}
