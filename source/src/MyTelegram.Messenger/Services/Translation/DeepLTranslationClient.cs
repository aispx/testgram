using System.Net;
using System.Text;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.Translation;

/// <summary>Why a translation did not happen. Maps one-to-one onto the errors the method documents.</summary>
public enum TextTranslationFailure
{
    None,

    /// <summary>
    /// <c>TRANSLATE_REQ_QUOTA_EXCEEDED</c> — "translation is currently unavailable due to a temporary
    /// server-side lack of resources". DeepL's 456 (character quota spent) and 429 (rate limited) are
    /// both exactly that from a client's point of view.
    /// </summary>
    QuotaExceeded,

    /// <summary><c>TRANSLATION_TIMEOUT</c>.</summary>
    Timeout,

    /// <summary><c>TRANSLATE_REQ_FAILED</c>. Everything else, including a rejected key.</summary>
    Failed
}

/// <param name="Texts">One entry per input, in input order. Non-null exactly when the call succeeded.</param>
/// <param name="Error">What to log. Never sent to a client — a bad key is not the caller's mistake.</param>
public sealed record TextTranslationOutcome(
    IReadOnlyList<string>? Texts,
    TextTranslationFailure Failure,
    string? Error)
{
    public bool Succeeded => Texts != null;

    public static TextTranslationOutcome Success(IReadOnlyList<string> texts) =>
        new(texts, TextTranslationFailure.None, null);

    public static TextTranslationOutcome Fail(TextTranslationFailure failure, string error) =>
        new(null, failure, error);
}

/// <summary>
/// Text translation over an external HTTP API, selected by <c>App__Translation__Provider</c>.
///
/// <para><b>DeepL</b> takes <c>POST {BaseUrl}/translate</c> with
/// <c>Authorization: DeepL-Auth-Key …</c> and form-urlencoded fields, the texts as repeated <c>text</c>
/// entries — so a batch of twenty messages is one call, which is what makes Android's
/// <c>MAX_MESSAGES_PER_REQUEST</c> batching cheap. A key ending in <c>:fx</c> is free-tier and only
/// authenticates against <c>api-free.deepl.com</c>.</para>
/// </summary>
public interface ITextTranslationClient
{
    /// <summary>False when there is no key or the feature is switched off.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Translates every entry, preserving order and count.
    /// </summary>
    /// <param name="texts">Plain text, or the markup <see cref="ITranslationEntityCodec"/> produced.</param>
    /// <param name="targetLanguage">A provider language code, already resolved by <c>TranslationLanguageMap</c>.</param>
    /// <param name="formality">The provider's formality value, or null.</param>
    /// <param name="html">Whether <paramref name="texts"/> carries inline markup to be repositioned.</param>
    Task<TextTranslationOutcome> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage,
        string? formality, bool html, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class DeepLTranslationClient(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<DeepLTranslationClient> logger)
    : ITextTranslationClient, ISingletonDependency
{
    /// <summary>
    /// One client for the process, as <c>SpeechRecognitionClient</c> and <c>TenorGifClient</c> do. The
    /// per-call deadline is a linked token rather than HttpClient.Timeout so it can be reconfigured
    /// without a restart.
    /// </summary>
    private static readonly HttpClient HttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>DeepL refuses more than 50 texts in one call, whatever the configured batch size is.</summary>
    private const int ProviderBatchLimit = 50;

    private TranslationConfig Config => options.CurrentValue.Translation;

    public bool IsEnabled
    {
        get
        {
            var config = Config;

            return config.Enabled
                   && !string.IsNullOrWhiteSpace(config.ApiKey)
                   && !string.IsNullOrWhiteSpace(config.BaseUrl);
        }
    }

    public async Task<TextTranslationOutcome> TranslateAsync(IReadOnlyList<string> texts,
        string targetLanguage, string? formality, bool html, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return TextTranslationOutcome.Fail(TextTranslationFailure.Failed,
                "translation is not configured (App__Translation__ApiKey/BaseUrl)");
        }

        if (texts.Count == 0)
        {
            return TextTranslationOutcome.Success([]);
        }

        var results = new List<string>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += ProviderBatchLimit)
        {
            var chunk = texts.Skip(offset).Take(ProviderBatchLimit).ToList();
            var outcome = await TranslateChunkAsync(chunk, targetLanguage, formality, html, cancellationToken);

            if (!outcome.Succeeded)
            {
                return outcome;
            }

            results.AddRange(outcome.Texts!);
        }

        return TextTranslationOutcome.Success(results);
    }

    private async Task<TextTranslationOutcome> TranslateChunkAsync(List<string> texts, string targetLanguage,
        string? formality, bool html, CancellationToken cancellationToken)
    {
        var config = Config;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        var body = new StringBuilder();

        foreach (var text in texts)
        {
            Append(body, "text", text);
        }

        Append(body, "target_lang", targetLanguage);

        if (html)
        {
            Append(body, "tag_handling", "html");
        }

        // Keeps DeepL from "correcting" the punctuation and leading case of a chat message.
        Append(body, "preserve_formatting", "1");

        if (formality != null && TranslationLanguageMap.SupportsFormality(targetLanguage))
        {
            Append(body, "formality", formality);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{config.BaseUrl.TrimEnd('/')}/translate")
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8,
                "application/x-www-form-urlencoded")
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {config.ApiKey}");

        try
        {
            using var response = await HttpClient.SendAsync(request, timeout.Token);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return Classify(response.StatusCode, payload);
            }

            return Parse(payload, texts.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TextTranslationOutcome.Fail(TextTranslationFailure.Timeout,
                $"the translation provider did not answer within {config.TimeoutSeconds}s");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Translation request failed");

            return TextTranslationOutcome.Fail(TextTranslationFailure.Failed, ex.Message);
        }
    }

    /// <summary>
    /// DeepL reports errors as <c>{"message": "…"}</c>. 456 is the monthly character quota and 429 is
    /// the rate limit — the two the caller can do nothing about but retry later. 401/403 is a bad or
    /// unauthorised key and 400 is a request this server built wrong; neither is the caller's fault, so
    /// both are logged and reported as the generic failure rather than as a client error.
    /// </summary>
    private TextTranslationOutcome Classify(HttpStatusCode status, string payload)
    {
        var message = ReadMessage(payload);
        var code = (int)status;

        if (code == 456 || status == HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning("Translation provider is out of quota or rate limited: {Status} {Message}",
                code, message);

            return TextTranslationOutcome.Fail(TextTranslationFailure.QuotaExceeded, message);
        }

        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            return TextTranslationOutcome.Fail(TextTranslationFailure.Timeout, message);
        }

        logger.LogError("Translation provider refused the request: {Status} {Message}", code, message);

        return TextTranslationOutcome.Fail(TextTranslationFailure.Failed, $"{code} {message}");
    }

    private static string ReadMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? payload;
            }
        }
        catch (JsonException)
        {
            // Not JSON — a gateway in front of the provider, most likely.
        }

        return payload.Length > 300 ? payload[..300] : payload;
    }

    /// <summary>
    /// A response with a different number of translations than texts cannot be handed on: every client
    /// pairs the vector with its request positionally (tdlib rejects a mismatch outright with
    /// "Receive invalid number of results", Android silently mismaps), so a short answer is a failure.
    /// </summary>
    private TextTranslationOutcome Parse(string payload, int expected)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
            {
                return TextTranslationOutcome.Fail(TextTranslationFailure.Failed,
                    "the translation provider returned no translations array");
            }

            var texts = new List<string>(expected);

            foreach (var translation in translations.EnumerateArray())
            {
                texts.Add(translation.TryGetProperty("text", out var text)
                    ? text.GetString() ?? string.Empty
                    : string.Empty);
            }

            if (texts.Count != expected)
            {
                logger.LogError(
                    "Translation provider returned {Actual} translations for {Expected} texts", texts.Count,
                    expected);

                return TextTranslationOutcome.Fail(TextTranslationFailure.Failed,
                    $"expected {expected} translations, got {texts.Count}");
            }

            return TextTranslationOutcome.Success(texts);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Could not parse the translation provider's response");

            return TextTranslationOutcome.Fail(TextTranslationFailure.Failed, ex.Message);
        }
    }

    private static void Append(StringBuilder body, string key, string value)
    {
        if (body.Length > 0)
        {
            body.Append('&');
        }

        body.Append(key).Append('=').Append(Uri.EscapeDataString(value));
    }
}
