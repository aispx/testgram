using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// The built-in <c>@gif</c> inline bot, which is what <c>config.gif_search_username</c> points at.
///
/// <para>"Entering text in the search bar should replace the saved GIFs list with the results of the
/// GIF search, which must be implemented as an inline query to the bot specified in
/// config.gif_search_username, with peer = inputPeerEmpty and query set equal to the input of the
/// user."</para>
///
/// <para>It is served in-process rather than by an external webhook, the same way BotFather is: it has
/// no MTProto session, so <c>messages.getInlineBotResults</c> short-circuits to this service instead of
/// pushing <c>updateBotInlineQuery</c> and waiting for an answer that would never come.</para>
/// See https://corefork.telegram.org/api/gifs#searching-gifs
/// </summary>
public interface IGifSearchBotService
{
    /// <summary>
    /// Answers an inline query by storing results under <paramref name="queryId"/> in the same place
    /// <c>messages.setInlineBotResults</c> writes them, so the ordinary read-back and
    /// <c>messages.sendInlineBotResult</c> paths work unchanged.
    /// </summary>
    Task AnswerAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetInlineBotResults request,
        long queryId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class GifSearchBotService(
    IMongoDatabase mongoDatabase,
    ITenorGifClient tenorClient,
    IGifDocumentReader documentReader,
    ISavedGifStore savedGifStore,
    IUserLanguageResolver userLanguageResolver,
    WebFiles.IWebDocumentProxy webDocumentProxy,
    WebFiles.IWebFileRegistrar webFileRegistrar,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<GifSearchBotService> logger)
    : IGifSearchBotService, ITransientDependency
{
    /// <summary>The bot that answers GIF search. Matches <c>config.gif_search_username = "gif"</c>.</summary>
    public const long BotUserId = MyTelegramConsts.GifSearchBotUserId;

    private const string ResultsCollection = "inline_bot_results";
    private static readonly TimeSpan ResultsRetention = TimeSpan.FromHours(1);

    public async Task AnswerAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetInlineBotResults request, long queryId,
        CancellationToken cancellationToken = default)
    {
        var config = options.CurrentValue.Gifs;
        var query = request.Query?.Trim() ?? string.Empty;

        // A non-empty offset means the client is scrolling for more of the same query.
        var isFirstPage = string.IsNullOrEmpty(request.Offset);

        var inputResults = new TVector<IInputBotInlineResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // Both sources are asked at once: the local lookup is two Mongo queries and Tenor is a quarter
        // of a second over the network, and waiting for the first before starting the second would add
        // that up. The share reserved for local results is fixed rather than measured for the same
        // reason - it has to be known before the Tenor call goes out.
        var localLimit = isFirstPage ? config.LocalResultLimit : 0;
        var tenorLimit = Math.Max(0, config.ResultLimit - localLimit);

        // Tenor ranks by locale, and the inline query does not carry one, so it comes from the
        // language the user's own client reports.
        var tenorTask = tenorLimit == 0
            ? Task.FromResult(new TenorSearchResult([], null))
            : SearchTenorAsync(input.UserId, query, request.Offset, tenorLimit, cancellationToken);

        // This server's own GIFs come first: they are already documents, so they send instantly and
        // are the only results available at all when Tenor is switched off or unreachable. Only on the
        // first page — repeating them on every page would push the same tiles back into view as the
        // user scrolls.
        foreach (var result in await BuildLocalResultsAsync(query, localLimit, cancellationToken))
        {
            if (seenIds.Add(GetId(result)))
            {
                inputResults.Add(result);
            }
        }

        var tenor = await tenorTask;
        var nextOffset = tenor.NextPosition;

        foreach (var gif in tenor.Gifs)
        {
            var result = BuildTenorResult(gif);
            if (seenIds.Add(gif.Id))
            {
                inputResults.Add(result);
            }
        }

        logger.LogDebug("GIF search for '{Query}' (offset '{Offset}') answered with {Count} results",
            query, request.Offset, inputResults.Count);

        await RegisterPreviewsAsync(input.UserId, tenor.Gifs, cancellationToken);

        await StoreAsync(input, request, queryId, inputResults, nextOffset, config, cancellationToken);
    }

    /// <summary>
    /// Hands the preview URLs to the file server before the results are converted, because a
    /// <c>webDocument</c> may only be proxied once the file server has the body — its
    /// <c>upload.getWebFile</c> answers <c>WEBDOCUMENT_INVALID</c> for a URL it does not know, and a
    /// client that cannot read the preview shows an empty tile.
    ///
    /// <para>Registration is concurrent because it is a download per URL on the file server, and it is
    /// remembered there and here, so the repeat queries a client fires while a word is typed cost
    /// nothing.</para>
    /// </summary>
    private async Task RegisterPreviewsAsync(long userId, List<TenorGif> gifs,
        CancellationToken cancellationToken)
    {
        var pending = gifs
            .Where(gif => !string.IsNullOrWhiteSpace(gif.ThumbUrl))
            .Select(gif => webFileRegistrar.EnsureRegisteredAsync(userId, gif.ThumbUrl, gif.ThumbMimeType,
                gif.ThumbSize, BuildThumbAttributes(gif), cancellationToken))
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        await Task.WhenAll(pending);
    }

    private async Task<TenorSearchResult> SearchTenorAsync(long userId, string query, string? offset, int limit,
        CancellationToken cancellationToken)
    {
        var language = await userLanguageResolver.GetLanguageAsync(userId);

        return await tenorClient.SearchAsync(query, offset, limit, language, cancellationToken);
    }

    /// <summary>
    /// GIFs stored on this server, matched on the filename recorded with the document and ranked by
    /// how many users have them saved, so the ones people actually use surface first.
    /// </summary>
    private async Task<List<IInputBotInlineResult>> BuildLocalResultsAsync(string query, int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        var documents = await documentReader.SearchAnimatedAsync(query, limit, cancellationToken);
        if (documents.Count == 0)
        {
            return [];
        }

        var savers = await savedGifStore.CountSaversAsync(documents.ConvertAll(p => p.DocumentId),
            cancellationToken);

        return documents
            .OrderByDescending(p => savers.GetValueOrDefault(p.DocumentId))
            .ThenByDescending(p => p.Date)
            .Select(IInputBotInlineResult (document) => new TInputBotInlineResultDocument
            {
                Id = document.DocumentId.ToString(),
                Type = "gif",
                Document = new TInputDocument
                {
                    Id = document.DocumentId,
                    AccessHash = document.AccessHash,
                    FileReference = document.FileReference
                },
                SendMessage = new TInputBotInlineMessageMediaAuto { Message = string.Empty }
            })
            .ToList();
    }

    /// <summary>
    /// A Tenor hit, referenced by URL. <c>inputWebDocument</c> becomes <c>webDocumentNoProxy</c> on the
    /// way out, which tells the client to fetch the preview itself — correct here, because this server
    /// does not proxy media. The animation is only imported once the user picks the result.
    ///
    /// <para>The grid tile plays the small MPEG4 in <c>thumb</c>, not the full animation in
    /// <c>content</c>: a page of thirty results is a few hundred kilobytes that way instead of several
    /// megabytes, and the full rendition is only fetched by whoever the GIF is sent to.</para>
    /// </summary>
    private static IInputBotInlineResult BuildTenorResult(TenorGif gif)
    {
        var videoAttribute = new TDocumentAttributeVideo
        {
            W = gif.Width,
            H = gif.Height,
            Duration = gif.DurationSeconds,
            Nosound = true,
            SupportsStreaming = true
        };

        return new TInputBotInlineResult
        {
            Id = gif.Id,
            Type = "gif",
            Title = gif.Description,
            Content = new TInputWebDocument
            {
                Url = gif.Mp4Url,
                Size = gif.Mp4Size,
                MimeType = GifDocumentHelper.Mp4MimeType,
                Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeAnimated(), videoAttribute)
            },
            Thumb = string.IsNullOrWhiteSpace(gif.ThumbUrl)
                ? null
                : new TInputWebDocument
                {
                    Url = gif.ThumbUrl,
                    Size = gif.ThumbSize,
                    MimeType = gif.ThumbMimeType,
                    Attributes = BuildThumbAttributes(gif)
                },
            SendMessage = new TInputBotInlineMessageMediaAuto { Message = string.Empty }
        };
    }

    /// <summary>
    /// Clients size the tile from the preview's own dimensions, so they have to travel with it: an
    /// MPEG4 preview carries them on <c>documentAttributeVideo</c>, a still image on
    /// <c>documentAttributeImageSize</c>.
    /// </summary>
    private static TVector<IDocumentAttribute> BuildThumbAttributes(TenorGif gif)
    {
        var width = gif.ThumbWidth > 0 ? gif.ThumbWidth : gif.Width;
        var height = gif.ThumbHeight > 0 ? gif.ThumbHeight : gif.Height;

        if (!string.Equals(gif.ThumbMimeType, GifDocumentHelper.Mp4MimeType, StringComparison.OrdinalIgnoreCase))
        {
            return new TVector<IDocumentAttribute>(new TDocumentAttributeImageSize { W = width, H = height });
        }

        return new TVector<IDocumentAttribute>(new TDocumentAttributeAnimated(),
            new TDocumentAttributeVideo
            {
                W = width,
                H = height,
                Duration = gif.DurationSeconds,
                Nosound = true,
                SupportsStreaming = true
            });
    }

    /// <summary>
    /// Writes the answer where <c>messages.setInlineBotResults</c> would have written it, so the
    /// read-back in <c>messages.getInlineBotResults</c> and the send path both stay generic.
    /// </summary>
    private async Task StoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetInlineBotResults request, long queryId,
        TVector<IInputBotInlineResult> inputResults, string? nextOffset, GifsConfig config,
        CancellationToken cancellationToken)
    {
        var converted = new TVector<IBotInlineResult>();

        foreach (var inputResult in inputResults)
        {
            var document = inputResult is TInputBotInlineResultDocument documentResult &&
                           documentResult.Document is TInputDocument inputDocument
                ? await documentReader.GetAsync(inputDocument.Id, cancellationToken)
                : null;

            var result = Bots.InlineResultConverter.ToBotInlineResult(inputResult,
                document: document == null ? null : documentReader.Map(document),
                urlSigner: webDocumentProxy);

            if (result != null)
            {
                converted.Add(result);
            }
        }

        await mongoDatabase.GetCollection<BsonDocument>(ResultsCollection).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"inline-results-{queryId}"),
            new BsonDocument
            {
                ["_id"] = $"inline-results-{queryId}",
                ["query_id"] = queryId,
                ["bot_id"] = BotUserId,
                ["user_id"] = input.UserId,
                ["query"] = request.Query ?? string.Empty,
                // A grid, like the sticker panel - "the GIF search UI should be almost identical to
                // the sticker search UI".
                ["gallery"] = true,
                ["private"] = false,
                ["cache_time"] = config.CacheTimeSeconds,
                ["next_offset"] = nextOffset ?? string.Empty,
                ["switch_pm"] = Array.Empty<byte>(),
                ["switch_webview"] = Array.Empty<byte>(),
                ["results"] = converted.ToBytes(),
                ["input_results"] = inputResults.ToBytes(),
                ["expires_at"] = DateTime.UtcNow.Add(ResultsRetention).ToTimestamp()
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    private static string GetId(IInputBotInlineResult result)
    {
        return result switch
        {
            TInputBotInlineResult r => r.Id,
            TInputBotInlineResultDocument r => r.Id,
            _ => string.Empty
        };
    }
}
