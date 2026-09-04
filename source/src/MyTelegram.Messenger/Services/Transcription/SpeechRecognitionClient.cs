using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.Transcription;

/// <summary>What recognition produced.</summary>
/// <param name="Text">
/// The transcript. May legitimately be empty — a voice note that is three seconds of silence has no
/// words in it, and that is a finished transcription, not a failure.
/// </param>
/// <param name="Language">Detected language, when the model reports one. Cached, never sent to clients.</param>
public sealed record SpeechRecognitionResult(string Text, string? Language);

/// <param name="Result">Non-null on success.</param>
/// <param name="Retryable">
/// Whether another attempt could succeed. A rate limit or a 5xx is worth retrying inside tdlib's 60
/// second window; a rejected file or a bad key is not.
/// </param>
/// <param name="Error">What to put in the log. Never sent to a client.</param>
public sealed record SpeechRecognitionOutcome(SpeechRecognitionResult? Result, bool Retryable, string? Error)
{
    public bool Succeeded => Result != null;

    public static SpeechRecognitionOutcome Success(string text, string? language) =>
        new(new SpeechRecognitionResult(text, language), false, null);

    public static SpeechRecognitionOutcome Fail(string error, bool retryable) => new(null, retryable, error);
}

/// <summary>
/// Speech recognition over an external HTTP API, in one of two shapes selected by
/// <c>App__Transcription__Provider</c>.
///
/// <para><b>Deepgram</b> (the default) takes <c>POST {BaseUrl}/listen</c> with <c>Authorization: Token …</c>
/// and the audio as the <b>raw request body</b>. Measured against the live API: it transcribes a Telegram
/// voice note (OGG OPUS, <c>Content-Type: audio/ogg</c>) and a round video note
/// (<c>video/mp4</c>, audio extracted from the container) as they are, in under a second, and
/// <c>detect_language=true</c> reports the language it found — so nothing has to be transcoded and ffmpeg
/// is not on this path at all.</para>
///
/// <para><b>OpenAI-compatible</b> takes <c>POST {BaseUrl}/audio/transcriptions</c> with
/// <c>Authorization: Bearer …</c> and <c>multipart/form-data</c>. That shape reaches OpenAI, VoidAI and most
/// self-hosted whisper servers, but it <b>refuses OGG</b> — measured against VoidAI, whose documentation
/// claims otherwise: <c>unsupported audio format. Supported: mp3, mp4, mpeg, mpga, m4a, wav, webm, flac</c>.
/// The body is therefore transcoded to MP3 before it gets here (see
/// <see cref="ITranscriptionAudioPreparer"/>).</para>
/// </summary>
public interface ISpeechRecognitionClient
{
    /// <summary>False when there is no key or the feature is switched off.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether the configured provider takes a body of this type unchanged. False means the caller has to
    /// transcode it first, which needs ffmpeg.
    /// </summary>
    bool AcceptsAsIs(string? mimeType);

    Task<SpeechRecognitionOutcome> RecognizeAsync(string path, string fileName, string contentType,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class SpeechRecognitionClient(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<SpeechRecognitionClient> logger)
    : ISpeechRecognitionClient, ISingletonDependency
{
    /// <summary>
    /// One client for the process, as <c>TenorGifClient</c> and <c>StoredFileStorage</c> do: a new
    /// HttpClient per call exhausts sockets. The per-call deadline is a linked CancellationToken instead
    /// of HttpClient.Timeout so it can be reconfigured without a restart.
    /// </summary>
    private static readonly HttpClient HttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Containers Deepgram was seen to accept as they are. Anything else is transcoded rather than sent on
    /// a guess: a body it cannot parse comes back as
    /// <c>failed to process audio: corrupt or unsupported data</c>, which is not retryable and costs the
    /// caller the whole request.
    /// </summary>
    private static readonly HashSet<string> DeepgramMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/ogg", "audio/opus", "audio/ogg; codecs=opus", "audio/webm", "audio/mpeg", "audio/mp3",
        "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/aac", "audio/wav", "audio/x-wav", "audio/wave",
        "audio/flac", "audio/x-flac", "video/mp4", "video/quicktime", "video/webm"
    };

    private TranscriptionConfig Config => options.CurrentValue.Transcription;

    public bool IsEnabled
    {
        get
        {
            var config = Config;

            return config.Enabled
                   && !string.IsNullOrWhiteSpace(config.ApiKey)
                   && !string.IsNullOrWhiteSpace(config.BaseUrl)
                   && !string.IsNullOrWhiteSpace(config.Model);
        }
    }

    public bool AcceptsAsIs(string? mimeType)
    {
        if (Config.Provider != TranscriptionProvider.Deepgram || string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        // A parameterised type ("audio/ogg; codecs=opus") is the same container.
        var bare = mimeType.Split(';')[0].Trim();

        return DeepgramMimeTypes.Contains(bare);
    }

    public async Task<SpeechRecognitionOutcome> RecognizeAsync(string path, string fileName, string contentType,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return SpeechRecognitionOutcome.Fail(
                "speech recognition is not configured (App__Transcription__ApiKey/Model/BaseUrl)", false);
        }

        var config = Config;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)));

        try
        {
            using var request = config.Provider == TranscriptionProvider.Deepgram
                ? BuildDeepgramRequest(config, path, contentType)
                : BuildOpenAiRequest(config, path, fileName, contentType);

            using var response = await HttpClient.SendAsync(request, timeout.Token);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                // 429 and 5xx are worth another attempt; 400/401/403 mean the request or the key is wrong
                // and repeating it only burns time out of the 60 seconds tdlib gives us.
                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                                || (int)response.StatusCode >= 500;

                return SpeechRecognitionOutcome.Fail(
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {Trim(payload)}", retryable);
            }

            return config.Provider == TranscriptionProvider.Deepgram
                ? ParseDeepgram(payload)
                : ParseOpenAi(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpeechRecognitionOutcome.Fail(
                $"the recognition provider did not answer within {config.TimeoutSeconds}s", true);
        }
        catch (HttpRequestException ex)
        {
            return SpeechRecognitionOutcome.Fail($"the recognition provider could not be reached: {ex.Message}", true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "The prepared audio {Path} could not be read for recognition", path);

            return SpeechRecognitionOutcome.Fail($"the prepared audio could not be read: {ex.Message}", false);
        }
    }

    /// <summary>
    /// Deepgram: the audio is the body, everything else is a query parameter, and the scheme is
    /// <c>Token</c> rather than <c>Bearer</c>.
    /// </summary>
    private static HttpRequestMessage BuildDeepgramRequest(TranscriptionConfig config, string path,
        string contentType)
    {
        var url = $"{config.BaseUrl.TrimEnd('/')}/listen?model={Uri.EscapeDataString(config.Model)}";

        if (config.SmartFormat)
        {
            url += "&smart_format=true";
        }

        if (config.DetectLanguage)
        {
            url += "&detect_language=true";
        }

        var body = new StreamContent(File.OpenRead(path));
        body.Headers.ContentType = MediaType(contentType);

        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = body,
            Headers = { Authorization = new AuthenticationHeaderValue("Token", config.ApiKey) }
        };
    }

    private static HttpRequestMessage BuildOpenAiRequest(TranscriptionConfig config, string path, string fileName,
        string contentType)
    {
        var url = $"{config.BaseUrl.TrimEnd('/')}/audio/transcriptions";

        // verbose_json is what carries the detected language, but only the whisper family accepts it -
        // the gpt-4o transcribe models take json/text only and answer 400 for anything else.
        var responseFormat = config.Model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase)
            ? "verbose_json"
            : "json";

        var content = new MultipartFormDataContent();

        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = MediaType(contentType);
        // Quoted explicitly. MultipartFormDataContent writes `name=file; filename=voice.mp3` when the
        // values need no escaping, while curl, the OpenAI SDKs and every other client of this endpoint
        // send `name="file"; filename="voice.mp3"` - and RFC 7578 says the quoted form. A gateway that
        // only accepts the common spelling would answer "no file provided" for a request that looks
        // perfectly well formed here.
        file.Headers.ContentDisposition = Disposition("file", fileName);
        content.Add(file);

        content.Add(Field("model", config.Model));
        content.Add(Field("response_format", responseFormat));

        return new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey) }
        };
    }

    /// <summary>
    /// <c>results.channels[0].alternatives[0].transcript</c>, with the language from
    /// <c>results.channels[0].detected_language</c>. An error body is
    /// <c>{"err_code":…,"err_msg":…,"request_id":…}</c>.
    /// </summary>
    private static SpeechRecognitionOutcome ParseDeepgram(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SpeechRecognitionOutcome.Fail("the recognition provider returned an empty body", true);
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("err_msg", out var error))
            {
                return SpeechRecognitionOutcome.Fail(
                    $"the recognition provider returned an error: {error.GetString()}", false);
            }

            if (!root.TryGetProperty("results", out var results)
                || !results.TryGetProperty("channels", out var channels)
                || channels.ValueKind != JsonValueKind.Array
                || channels.GetArrayLength() == 0)
            {
                return SpeechRecognitionOutcome.Fail(
                    $"the recognition provider returned no channels: {Trim(payload)}", false);
            }

            var channel = channels[0];

            var language = channel.TryGetProperty("detected_language", out var detected)
                           && detected.ValueKind == JsonValueKind.String
                ? detected.GetString()
                : null;

            if (!channel.TryGetProperty("alternatives", out var alternatives)
                || alternatives.ValueKind != JsonValueKind.Array
                || alternatives.GetArrayLength() == 0)
            {
                // A channel with no alternatives at all is a finished recognition of nothing, which is what
                // silence sounds like.
                return SpeechRecognitionOutcome.Success(string.Empty, language);
            }

            if (!alternatives[0].TryGetProperty("transcript", out var transcript)
                || transcript.ValueKind != JsonValueKind.String)
            {
                return SpeechRecognitionOutcome.Fail(
                    $"the recognition provider returned no transcript: {Trim(payload)}", false);
            }

            return SpeechRecognitionOutcome.Success(transcript.GetString() ?? string.Empty, language);
        }
        catch (JsonException)
        {
            return SpeechRecognitionOutcome.Fail(
                $"the recognition provider returned a body that is not JSON: {Trim(payload)}", false);
        }
    }

    /// <summary>
    /// Both OpenAI response shapes carry <c>text</c>; <c>verbose_json</c> adds <c>language</c>. A body that
    /// is not JSON at all is treated as plain text, which is what <c>response_format=text</c> returns.
    /// </summary>
    private static SpeechRecognitionOutcome ParseOpenAi(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SpeechRecognitionOutcome.Fail("the recognition provider returned an empty body", true);
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return SpeechRecognitionOutcome.Success(payload.Trim(), null);
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.Object
                              && error.TryGetProperty("message", out var text)
                              && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : error.ToString();

                return SpeechRecognitionOutcome.Fail($"the recognition provider returned an error: {message}", false);
            }

            if (!root.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return SpeechRecognitionOutcome.Fail(
                    $"the recognition provider returned no text: {Trim(payload)}", false);
            }

            var language = root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                ? lang.GetString()
                : null;

            return SpeechRecognitionOutcome.Success(value.GetString() ?? string.Empty, language);
        }
        catch (JsonException)
        {
            // response_format=text, or a proxy that answered with something else entirely. A plain body
            // is the transcript.
            return SpeechRecognitionOutcome.Success(payload.Trim(), null);
        }
    }

    /// <summary>
    /// A stored mime type comes from a client and may be anything at all, so a malformed one must not throw
    /// on the way out.
    /// </summary>
    private static MediaTypeHeaderValue MediaType(string contentType)
    {
        return MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue("application/octet-stream");
    }

    /// <summary>
    /// A <c>Content-Disposition: form-data</c> header whose values are quoted, which is the spelling every
    /// other client of this endpoint uses.
    /// </summary>
    private static ContentDispositionHeaderValue Disposition(string name, string? fileName = null)
    {
        var disposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };

        if (fileName != null)
        {
            disposition.FileName = $"\"{fileName}\"";
        }

        return disposition;
    }

    private static StringContent Field(string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentDisposition = Disposition(name);

        return content;
    }

    private static string Trim(string payload)
    {
        var single = payload.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return single.Length <= 300
            ? single
            : string.Concat(single.AsSpan(0, 300), "…".AsSpan());
    }
}
