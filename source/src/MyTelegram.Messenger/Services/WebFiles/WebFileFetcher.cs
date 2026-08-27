using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.WebFiles;

/// <summary>A web file body this server has fetched on a client's behalf.</summary>
/// <param name="Bytes">The whole body. Clients ask for it in slices.</param>
/// <param name="MimeType">What the origin reported, falling back to what the web document declared.</param>
public sealed record WebFileBody(byte[] Bytes, string MimeType);

/// <summary>
/// Fetches the body behind a proxied <c>webDocument</c> so <c>upload.getWebFile</c> can serve it.
///
/// <para>Clients read a file in slices of at most 512 KB and never in one call, so the body is fetched
/// once and kept: re-downloading it per slice would multiply the traffic to the origin by the number of
/// slices and make every slice as slow as the first.</para>
/// </summary>
public interface IWebFileFetcher
{
    /// <summary>
    /// The body for <paramref name="url"/>, from the cache when it is there. Null when the origin
    /// refused it, it was too large, or it could not be reached.
    /// </summary>
    Task<WebFileBody?> GetAsync(string url, string? declaredMimeType,
        CancellationToken cancellationToken = default);

    /// <summary>Whether this server is willing to fetch <paramref name="url"/> at all.</summary>
    bool IsAllowed(string url);
}

/// <inheritdoc />
public class WebFileFetcher(
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<WebFileFetcher> logger)
    : IWebFileFetcher, ISingletonDependency
{
    public const string CollectionName = "web_file_cache";

    /// <summary>
    /// Redirects are not followed: the URL that was signed is the URL that gets fetched, and a redirect
    /// is how a signed public URL would turn into a request to somewhere else.
    /// </summary>
    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false });

    private static int _indexInitialized;

    private WebFilesConfig Config => options.CurrentValue.WebFiles;

    public bool IsAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;

        return Config.AllowedHosts.Any(allowed =>
            host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{allowed}", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<WebFileBody?> GetAsync(string url, string? declaredMimeType,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(url))
        {
            logger.LogWarning("A web file was requested for a host that is not proxied");

            return null;
        }

        var cached = await ReadCacheAsync(url, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        var body = await DownloadAsync(url, declaredMimeType, cancellationToken);
        if (body != null)
        {
            await WriteCacheAsync(url, body, cancellationToken);
        }

        return body;
    }

    private async Task<WebFileBody?> DownloadAsync(string url, string? declaredMimeType,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Config.TimeoutSeconds)));

            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The origin answered {Status} for a proxied web file", (int)response.StatusCode);

                return null;
            }

            if (response.Content.Headers.ContentLength > Config.MaxBytes)
            {
                logger.LogWarning("A proxied web file is {Size} bytes, over the {Limit} byte limit",
                    response.Content.Headers.ContentLength, Config.MaxBytes);

                return null;
            }

            // Read through a bounded buffer rather than trusting Content-Length, which the origin is
            // free to understate.
            using var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token))
            {
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + read > Config.MaxBytes)
                    {
                        logger.LogWarning("A proxied web file exceeded the {Limit} byte limit while reading",
                            Config.MaxBytes);

                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            var mimeType = response.Content.Headers.ContentType?.MediaType;

            return new WebFileBody(buffer.ToArray(),
                string.IsNullOrWhiteSpace(mimeType)
                    ? declaredMimeType ?? "application/octet-stream"
                    : mimeType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A proxied web file could not be fetched");

            return null;
        }
    }

    private async Task<WebFileBody?> ReadCacheAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var document = await Collection()
                .Find(Builders<BsonDocument>.Filter.Eq("_id", CacheId(url)))
                .FirstOrDefaultAsync(cancellationToken);

            if (document == null)
            {
                return null;
            }

            return new WebFileBody(document["Bytes"].AsBsonBinaryData.Bytes,
                document.GetValue("MimeType", "application/octet-stream").AsString);
        }
        catch (Exception ex)
        {
            // A cache that cannot be read costs a download, not the request.
            logger.LogDebug(ex, "The web file cache could not be read");

            return null;
        }
    }

    private async Task WriteCacheAsync(string url, WebFileBody body, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureIndexAsync(cancellationToken);

            await Collection().ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", CacheId(url)),
                new BsonDocument
                {
                    ["_id"] = CacheId(url),
                    ["Url"] = url,
                    ["MimeType"] = body.MimeType,
                    ["Bytes"] = new BsonBinaryData(body.Bytes),
                    ["Size"] = body.Bytes.Length,
                    ["CachedAt"] = DateTime.UtcNow
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The web file cache could not be written");
        }
    }

    /// <summary>
    /// The cache expires on its own: these are previews of somebody else's catalogue, and keeping them
    /// forever would grow without bound.
    /// </summary>
    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _indexInitialized, 1) == 1)
        {
            return;
        }

        try
        {
            await Collection().Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("CachedAt"),
                    new CreateIndexOptions
                    {
                        Name = "web_file_cache_ttl",
                        ExpireAfter = TimeSpan.FromSeconds(Math.Max(60, Config.CacheSeconds))
                    }),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The web file cache index could not be created");
        }
    }

    private IMongoCollection<BsonDocument> Collection()
    {
        return mongoDatabase.GetCollection<BsonDocument>(CollectionName);
    }

    private static string CacheId(string url)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url)));
    }
}
