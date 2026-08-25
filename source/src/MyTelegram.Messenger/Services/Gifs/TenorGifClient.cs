using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>One GIF as Tenor describes it, reduced to what an inline result needs.</summary>
/// <param name="Id">Tenor's own id, reused as the inline result id and as the import cache key.</param>
/// <param name="Description">Alt text, shown by clients that render a title.</param>
/// <param name="Mp4Url">Silent MPEG4 — the file that is actually sent.</param>
/// <param name="Mp4Size">Byte size Tenor reports for the MPEG4.</param>
/// <param name="Width">Pixel width of the MPEG4.</param>
/// <param name="Height">Pixel height of the MPEG4.</param>
/// <param name="DurationSeconds">Duration of the MPEG4, rounded up.</param>
/// <param name="ThumbUrl">Small preview for the grid tile.</param>
/// <param name="ThumbSize">Byte size Tenor reports for the preview.</param>
/// <param name="ThumbMimeType">
/// Mime type of the preview. A grid tile is played, not just shown, so an MPEG4 preview is preferred:
/// Android picks <c>thumb</c> over <c>content</c> precisely when its mime type is <c>video/mp4</c>
/// (<c>ContextLinkCell</c>), and tdlib treats such a thumbnail as an animation
/// (<c>get_web_document_photo_size</c>). Falling back to a still image is what clients that only
/// decode images need.
/// </param>
/// <param name="ThumbWidth">Pixel width of the preview.</param>
/// <param name="ThumbHeight">Pixel height of the preview.</param>
public sealed record TenorGif(
    string Id,
    string? Description,
    string Mp4Url,
    int Mp4Size,
    int Width,
    int Height,
    int DurationSeconds,
    string? ThumbUrl,
    int ThumbSize,
    string ThumbMimeType,
    int ThumbWidth,
    int ThumbHeight);

/// <param name="Gifs">The page of results.</param>
/// <param name="NextPosition">Tenor's cursor, handed to clients as <c>botResults.next_offset</c>.</param>
public sealed record TenorSearchResult(List<TenorGif> Gifs, string? NextPosition);

/// <summary>
/// Tenor search, the provider Telegram credits through <c>appConfig.gif_search_branding</c>.
/// See https://developers.google.com/tenor/guides/endpoints
/// </summary>
public interface ITenorGifClient
{
    bool IsEnabled { get; }

    /// <summary>
    /// Searches Tenor, or returns the featured GIFs when <paramref name="query"/> is empty. Returns
    /// an empty page rather than throwing when Tenor is unreachable — GIF search then falls back to
    /// this server's own GIFs.
    /// </summary>
    Task<TenorSearchResult> SearchAsync(string? query, string? position, int limit, string? languageCode,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TenorGifClient(
    IConnectionMultiplexer redis,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<TenorGifClient> logger)
    : ITenorGifClient, ISingletonDependency
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler { AllowAutoRedirect = false });

    private TenorConfig Config => options.CurrentValue.Gifs.Tenor;

    public bool IsEnabled => Config.Enabled && !string.IsNullOrWhiteSpace(Config.ApiKey);

    public async Task<TenorSearchResult> SearchAsync(string? query, string? position, int limit,
        string? languageCode, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || limit <= 0)
        {
            return new TenorSearchResult([], null);
        }

        var config = Config;
        var trimmed = query?.Trim();
        var isSearch = !string.IsNullOrEmpty(trimmed);

        // media_filter keeps the payload to the renditions that are used. The animated `tinygif` is
        // deliberately not among them: at 0.3-1.4 MB per result it is larger than the MPEG4 it
        // previews, and a grid of thirty of those is minutes of loading on a phone.
        var url = $"{config.BaseUrl.TrimEnd('/')}/{(isSearch ? "search" : "featured")}" +
                  $"?key={Uri.EscapeDataString(config.ApiKey)}" +
                  $"&client_key={Uri.EscapeDataString(config.ClientKey)}" +
                  $"&contentfilter={Uri.EscapeDataString(config.ContentFilter)}" +
                  $"&media_filter={TenorGifParser.MediaFilter}" +
                  $"&limit={limit}";

        if (isSearch)
        {
            url += $"&q={Uri.EscapeDataString(trimmed!)}";
        }

        if (!string.IsNullOrWhiteSpace(position))
        {
            url += $"&pos={Uri.EscapeDataString(position)}";
        }

        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            url += $"&locale={Uri.EscapeDataString(languageCode)}";
        }

        // Clients re-query on every keystroke, so the same page is asked for again and again while a
        // word is being typed. Each round trip to Tenor is a quarter of a second and one more call
        // against the API quota, and the answer does not change in the meantime.
        var cacheKey = $"tenor:search:{isSearch}:{config.ContentFilter}:{languageCode}:{limit}:{position}:{trimmed}";
        var cached = await GetCachedAsync(cacheKey);
        if (cached != null)
        {
            return TenorGifParser.Parse(cached);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));

            var response = await HttpClient.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tenor answered {Status} for a GIF search", (int)response.StatusCode);
                return new TenorSearchResult([], null);
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            await SetCachedAsync(cacheKey, json);

            return TenorGifParser.Parse(json);
        }
        catch (Exception ex)
        {
            // GIF search must degrade to the local corpus rather than failing the inline query.
            logger.LogWarning(ex, "Tenor could not be reached for a GIF search");
            return new TenorSearchResult([], null);
        }
    }

    private async Task<string?> GetCachedAsync(string cacheKey)
    {
        try
        {
            return await redis.GetDatabase().StringGetAsync(cacheKey);
        }
        catch (Exception ex)
        {
            // A cache that cannot be read is a slow search, not a failed one.
            logger.LogDebug(ex, "The Tenor search cache could not be read");

            return null;
        }
    }

    private async Task SetCachedAsync(string cacheKey, string json)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(cacheKey, json,
                TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.Gifs.CacheTimeSeconds)));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The Tenor search cache could not be written");
        }
    }
}

/// <summary>
/// Turns a Tenor <c>/v2/search</c> or <c>/v2/featured</c> payload into inline-result material.
/// Separate from the HTTP client so the mapping can be exercised without a network call.
/// See https://developers.google.com/tenor/guides/response-objects-and-errors
/// </summary>
public static class TenorGifParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The renditions asked of Tenor, in the order they are preferred as the grid preview. The MPEG4
    /// ones come first because a grid tile is played rather than shown; <c>tinygifpreview</c> is a still
    /// PNG and is only there for clients that cannot animate a thumbnail.
    /// </summary>
    private static readonly (string Format, string MimeType)[] ThumbFormats =
    [
        ("nanomp4", GifDocumentHelper.Mp4MimeType),
        ("tinymp4", GifDocumentHelper.Mp4MimeType),
        ("tinygifpreview", "image/png"),
        ("nanogifpreview", "image/png")
    ];

    /// <summary>The <c>media_filter</c> value that fetches exactly the renditions used here.</summary>
    public static readonly string MediaFilter =
        string.Join(',', ThumbFormats.Select(p => p.Format).Prepend("mp4"));

    public static TenorSearchResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TenorSearchResult([], null);
        }

        TenorResponse? payload;
        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize<TenorResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new TenorSearchResult([], null);
        }

        var gifs = new List<TenorGif>();

        foreach (var result in payload?.Results ?? [])
        {
            if (string.IsNullOrWhiteSpace(result.Id) || result.MediaFormats == null)
            {
                continue;
            }

            // Without an MPEG4 there is nothing sendable, so the entry is skipped rather than
            // emitted half-built.
            if (!result.MediaFormats.TryGetValue("mp4", out var mp4) || string.IsNullOrWhiteSpace(mp4.Url))
            {
                continue;
            }

            var (thumb, thumbMimeType) = SelectThumb(result.MediaFormats);

            gifs.Add(new TenorGif(
                result.Id!,
                result.ContentDescription,
                mp4.Url!,
                mp4.Size,
                mp4.Dims is { Length: 2 } ? mp4.Dims[0] : 0,
                mp4.Dims is { Length: 2 } ? mp4.Dims[1] : 0,
                (int)Math.Ceiling(mp4.Duration),
                thumb?.Url,
                thumb?.Size ?? 0,
                thumbMimeType,
                thumb?.Dims is { Length: 2 } ? thumb.Dims[0] : 0,
                thumb?.Dims is { Length: 2 } ? thumb.Dims[1] : 0));
        }

        return new TenorSearchResult(gifs, string.IsNullOrWhiteSpace(payload?.Next) ? null : payload.Next);
    }

    private static (TenorMediaFormat? Thumb, string MimeType) SelectThumb(
        Dictionary<string, TenorMediaFormat> formats)
    {
        foreach (var (format, mimeType) in ThumbFormats)
        {
            if (formats.TryGetValue(format, out var candidate) && !string.IsNullOrWhiteSpace(candidate.Url))
            {
                return (candidate, mimeType);
            }
        }

        return (null, GifDocumentHelper.Mp4MimeType);
    }

    private sealed class TenorResponse
    {
        public List<TenorResult>? Results { get; set; }

        public string? Next { get; set; }
    }

    private sealed class TenorResult
    {
        public string? Id { get; set; }

        [JsonPropertyName("content_description")]
        public string? ContentDescription { get; set; }

        [JsonPropertyName("media_formats")]
        public Dictionary<string, TenorMediaFormat>? MediaFormats { get; set; }
    }

    private sealed class TenorMediaFormat
    {
        public string? Url { get; set; }

        public int Size { get; set; }

        public double Duration { get; set; }

        public int[]? Dims { get; set; }
    }
}
