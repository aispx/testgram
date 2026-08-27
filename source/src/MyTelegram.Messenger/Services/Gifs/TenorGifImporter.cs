using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>A Tenor GIF that has been imported into this server as a document.</summary>
[BsonIgnoreExtraElements]
public class TenorGifDocument
{
    /// <summary>Tenor's own id for the animation.</summary>
    [BsonId]
    public string TenorId { get; set; } = string.Empty;

    public long DocumentId { get; set; }

    public int Date { get; set; }
}

/// <summary>
/// Imports a GIF that was found through Tenor search into this server, so it can be sent like any
/// other document — and so it can then be saved, since the saved-GIF list holds document ids.
///
/// <para>Search itself does not import anything: results reference Tenor's own URLs through
/// <c>webDocumentNoProxy</c>, which is exactly "fetch this yourself". The import happens when the
/// user actually picks a result.</para>
/// </summary>
public interface ITenorGifImporter
{
    /// <summary>
    /// The document for a Tenor animation, downloading and registering it on first use and reusing it
    /// afterwards. Returns null when it could not be fetched or stored.
    /// </summary>
    /// <param name="info">
    /// Dimensions and duration, when the caller already knows them — the inline result carries what
    /// Tenor reported. Saves probing the file with ffprobe, which is a process spawn on the path a user
    /// is waiting on. Probed when null.
    /// </param>
    Task<TDocument?> ImportAsync(long userId, string tenorId, string mp4Url, VideoInfo? info = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TenorGifImporter(
    IMongoDatabase mongoDatabase,
    IGifDocumentPublisher documentPublisher,
    IGifDocumentReader documentReader,
    IVideoTranscoder videoTranscoder,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TenorGifImporter> logger)
    : ITenorGifImporter, ITransientDependency
{
    public const string CollectionName = "tenor_gifs";

    /// <summary>Tenor's own MPEG4 renditions are small; anything larger is not one of them.</summary>
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = true });

    private IMongoCollection<TenorGifDocument> Collection =>
        mongoDatabase.GetCollection<TenorGifDocument>(CollectionName);

    public async Task<TDocument?> ImportAsync(long userId, string tenorId, string mp4Url, VideoInfo? info = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenorId) || string.IsNullOrWhiteSpace(mp4Url))
        {
            return null;
        }

        // One document per Tenor animation, shared by everyone who sends it.
        var cached = await Collection
            .Find(Builders<TenorGifDocument>.Filter.Eq(p => p.TenorId, tenorId))
            .FirstOrDefaultAsync(cancellationToken);

        if (cached != null)
        {
            var existing = await documentReader.GetAsync(cached.DocumentId, cancellationToken);
            if (GifDocumentHelper.IsAnimatedMp4(existing))
            {
                return documentReader.Map(existing!);
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"tenor-{tenorId}-{Guid.NewGuid():N}.mp4");

        try
        {
            if (!await DownloadAsync(mp4Url, path, cancellationToken))
            {
                return null;
            }

            var resolved = info is { Width: > 0, Height: > 0 }
                ? info
                : await videoTranscoder.ProbeAsync(path, cancellationToken);
            var document = await documentPublisher.PublishAsync(userId, path, $"{tenorId}.mp4", resolved,
                cancellationToken);
            if (document == null)
            {
                return null;
            }

            await Collection.ReplaceOneAsync(
                Builders<TenorGifDocument>.Filter.Eq(p => p.TenorId, tenorId),
                new TenorGifDocument
                {
                    TenorId = tenorId,
                    DocumentId = document.Id,
                    Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);

            logger.LogInformation("Imported Tenor GIF {TenorId} as document {DocumentId}", tenorId, document.Id);

            return document;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Importing Tenor GIF {TenorId} failed", tenorId);
            return null;
        }
        finally
        {
            GifTempFile.Delete(path);
        }
    }

    private async Task<bool> DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.Gifs.Tenor.TimeoutSeconds)));

        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Tenor returned {Status} for the animation body", (int)response.StatusCode);
            return false;
        }

        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            logger.LogWarning("The Tenor animation body is {Size} bytes, which is more than expected",
                response.Content.Headers.ContentLength);
            return false;
        }

        await using (var destination = File.Create(path))
        {
            await response.Content.CopyToAsync(destination, timeout.Token);
        }

        var info = new FileInfo(path);

        return info.Exists && info.Length > 0 && info.Length <= MaxDownloadBytes;
    }
}
