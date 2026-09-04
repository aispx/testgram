using System.IO;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Transcription;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: the request <see cref="SpeechRecognitionClient"/> puts on the wire, in both provider shapes,
/// and what it makes of the answer.
///
/// <para>
/// Everything here is measurable except the provider itself, so it is exercised against a local listener
/// rather than discovered in production. The two shapes have nothing in common — Deepgram takes the audio
/// as the <b>raw body</b> with <c>Authorization: Token</c> and options in the query string, the
/// OpenAI-compatible endpoint takes <c>multipart/form-data</c> with <c>Authorization: Bearer</c> — and the
/// only thing that decides between them is <c>App__Transcription__Provider</c>.
/// </para>
///
/// <para>
/// The response shapes differ just as much: Deepgram answers
/// <c>results.channels[0].alternatives[0].transcript</c> and reports errors as
/// <c>{"err_code":…,"err_msg":…}</c> (measured: 401 <c>INVALID_AUTH</c>, 403
/// <c>INSUFFICIENT_PERMISSIONS</c>, 400 <c>failed to process audio</c>), while OpenAI answers a flat
/// <c>text</c> and <c>{"error":{"message":…}}</c>.
/// </para>
/// </summary>
public class SpeechRecognitionClientTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly List<string> _bodies = [];
    private readonly List<byte[]> _rawBodies = [];
    private readonly List<string?> _authorizations = [];
    private readonly List<string> _paths = [];
    private readonly List<string?> _contentTypes = [];

    private string _responseBody = "{\"text\":\"\"}";
    private int _statusCode = 200;

    public SpeechRecognitionClientTests()
    {
        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}/v1";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Close();
    }

    // ---------------------------------------------------------------- Deepgram

    private const string DeepgramSuccess = """
        {"metadata":{"duration":3.624,"channels":1},
         "results":{"channels":[{"detected_language":"de","alternatives":[
           {"transcript":"Guten Tag, dies ist eine Sprachnachricht.","confidence":0.98}]}]}}
        """;

    /// <summary>
    /// The audio is the body, not a form field, and the scheme is <c>Token</c> — a Bearer header is
    /// rejected outright by Deepgram.
    /// </summary>
    [Fact]
    public async Task Deepgram_gets_the_audio_as_the_raw_body_with_a_token_header()
    {
        _responseBody = DeepgramSuccess;

        var audio = Audio(out var bytes);
        var outcome = await Client(TranscriptionProvider.Deepgram, "nova-3")
            .RecognizeAsync(audio, "voice-1.ogg", "audio/ogg");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result!.Text.ShouldBe("Guten Tag, dies ist eine Sprachnachricht.");
        outcome.Result.Language.ShouldBe("de");

        _authorizations.ShouldHaveSingleItem().ShouldBe($"Token {ApiKey}");
        _rawBodies.ShouldHaveSingleItem().ShouldBe(bytes);
        _contentTypes.ShouldHaveSingleItem().ShouldBe("audio/ogg");
    }

    /// <summary>
    /// <c>smart_format</c> is what makes the transcript punctuated and capitalised, and
    /// <c>detect_language</c> is what makes it work for a chat where every message is in a different
    /// language. Both are query parameters, not body fields.
    /// </summary>
    [Fact]
    public async Task Deepgram_is_asked_for_smart_formatting_and_language_detection()
    {
        _responseBody = DeepgramSuccess;

        await Client(TranscriptionProvider.Deepgram, "nova-3")
            .RecognizeAsync(Audio(out _), "voice-1.ogg", "audio/ogg");

        var path = _paths.ShouldHaveSingleItem();
        path.ShouldStartWith("/v1/listen");
        path.ShouldContain("model=nova-3");
        path.ShouldContain("smart_format=true");
        path.ShouldContain("detect_language=true");
    }

    [Fact]
    public async Task Deepgram_options_can_be_switched_off()
    {
        _responseBody = DeepgramSuccess;

        await Client(TranscriptionProvider.Deepgram, "nova-2", detectLanguage: false, smartFormat: false)
            .RecognizeAsync(Audio(out _), "voice-1.ogg", "audio/ogg");

        var path = _paths.ShouldHaveSingleItem();
        path.ShouldContain("model=nova-2");
        path.ShouldNotContain("smart_format");
        path.ShouldNotContain("detect_language");
    }

    /// <summary>
    /// A round video note goes over as <c>video/mp4</c> and Deepgram pulls the audio out of the container
    /// itself — measured against the live API, which is why nothing transcodes it here.
    /// </summary>
    [Fact]
    public async Task Deepgram_takes_a_round_video_note_as_video_mp4()
    {
        _responseBody = DeepgramSuccess;

        await Client(TranscriptionProvider.Deepgram, "nova-3")
            .RecognizeAsync(Audio(out _), "voice-1.mp4", "video/mp4");

        _contentTypes.ShouldHaveSingleItem().ShouldBe("video/mp4");
    }

    /// <summary>Silence: an empty transcript at HTTP 200 is a finished recognition, not a failure.</summary>
    [Fact]
    public async Task Deepgram_silence_is_an_empty_success()
    {
        _responseBody =
            """{"results":{"channels":[{"alternatives":[{"transcript":"","confidence":0}]}]}}""";

        var outcome = await Client(TranscriptionProvider.Deepgram, "nova-3")
            .RecognizeAsync(Audio(out _), "voice-1.ogg", "audio/ogg");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result!.Text.ShouldBe(string.Empty);
    }

    /// <summary>
    /// The three failures the live API actually produces. None is retryable: the key, the model permission
    /// or the body is wrong, and repeating the call only burns the 60 seconds tdlib allows.
    /// </summary>
    [Theory]
    [InlineData(401, "INVALID_AUTH", "Invalid credentials.")]
    [InlineData(403, "INSUFFICIENT_PERMISSIONS", "Project does not have access to the requested model.")]
    [InlineData(400, "Bad Request", "Bad Request: failed to process audio: corrupt or unsupported data")]
    public async Task Deepgram_errors_are_reported_and_not_retried(int statusCode, string code, string message)
    {
        _statusCode = statusCode;
        _responseBody = $"{{\"err_code\":\"{code}\",\"err_msg\":\"{message}\",\"request_id\":\"01a0\"}}";

        var outcome = await Client(TranscriptionProvider.Deepgram, "nova-3")
            .RecognizeAsync(Audio(out _), "voice-1.ogg", "audio/ogg");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Retryable.ShouldBeFalse();
        outcome.Error.ShouldContain(message);
    }

    /// <summary>
    /// The whole reason the transcode is skipped: these are the containers the live API was seen to take.
    /// Anything outside the list is transcoded rather than sent on a guess, because a body Deepgram cannot
    /// parse comes back as a non-retryable 400.
    /// </summary>
    [Theory]
    [InlineData("audio/ogg", true)]
    [InlineData("audio/ogg; codecs=opus", true)]
    [InlineData("video/mp4", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("AUDIO/OGG", true)]
    [InlineData("audio/amr", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Deepgram_accepts_the_containers_it_was_measured_with(string? mimeType, bool accepted)
    {
        Client(TranscriptionProvider.Deepgram, "nova-3").AcceptsAsIs(mimeType).ShouldBe(accepted);
    }

    /// <summary>
    /// The OpenAI-compatible endpoint refuses OGG, so nothing may be sent to it unconverted — whatever the
    /// mime type says.
    /// </summary>
    [Theory]
    [InlineData("audio/ogg")]
    [InlineData("audio/mpeg")]
    [InlineData("video/mp4")]
    public void The_openai_shape_never_takes_a_body_unchanged(string mimeType)
    {
        Client(TranscriptionProvider.OpenAiCompatible, "whisper-1").AcceptsAsIs(mimeType).ShouldBeFalse();
    }

    // -------------------------------------------------------- OpenAI-compatible

    [Fact]
    public async Task The_openai_request_is_a_multipart_post_with_the_documented_fields()
    {
        _responseBody = "{\"task\":\"transcribe\",\"language\":\"russian\",\"duration\":3.4,\"text\":\"привет\"}";

        var outcome = await Client(TranscriptionProvider.OpenAiCompatible, "whisper-1")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result!.Text.ShouldBe("привет");
        outcome.Result.Language.ShouldBe("russian");

        _authorizations.ShouldHaveSingleItem().ShouldBe($"Bearer {ApiKey}");
        _paths.ShouldHaveSingleItem().ShouldBe("/v1/audio/transcriptions");

        var body = _bodies.ShouldHaveSingleItem();
        body.ShouldContain("name=\"file\"");
        body.ShouldContain("filename=\"voice-1.mp3\"");
        body.ShouldContain("name=\"model\"");
        body.ShouldContain("whisper-1");
        body.ShouldContain("name=\"response_format\"");
        body.ShouldContain("verbose_json");
    }

    /// <summary>
    /// The gpt-4o transcribe models reject <c>verbose_json</c>, so the format follows the model rather
    /// than being a constant.
    /// </summary>
    [Fact]
    public async Task A_gpt_4o_model_is_asked_for_plain_json()
    {
        await Client(TranscriptionProvider.OpenAiCompatible, "gpt-4o-mini-transcribe")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        var body = _bodies.ShouldHaveSingleItem();
        body.ShouldContain("gpt-4o-mini-transcribe");
        body.ShouldContain("name=\"response_format\"");
        body.ShouldNotContain("verbose_json");
    }

    [Fact]
    public async Task An_empty_openai_transcript_is_a_success()
    {
        _responseBody = "{\"text\":\"\"}";

        var outcome = await Client(TranscriptionProvider.OpenAiCompatible, "whisper-1")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result!.Text.ShouldBe(string.Empty);
    }

    /// <summary>
    /// 429 and 5xx are worth another attempt inside tdlib's 60 second window; a 400 or a 403 means the
    /// request or the key is wrong and repeating it only burns that window. This is the split the worker's
    /// retry decision rests on.
    /// </summary>
    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public async Task Only_a_rate_limit_or_a_server_error_is_retried(int statusCode, bool retryable)
    {
        _statusCode = statusCode;
        _responseBody = "{\"error\":{\"message\":\"The upstream provider denied the request\"}}";

        var outcome = await Client(TranscriptionProvider.OpenAiCompatible, "whisper-1")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Retryable.ShouldBe(retryable);
        outcome.Error.ShouldContain("upstream provider denied");
    }

    [Fact]
    public async Task An_error_object_in_a_successful_response_is_a_failure()
    {
        _responseBody = "{\"error\":{\"message\":\"unsupported audio format\",\"code\":\"invalid_parameter\"}}";

        var outcome = await Client(TranscriptionProvider.OpenAiCompatible, "whisper-1")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Retryable.ShouldBeFalse();
        outcome.Error.ShouldContain("unsupported audio format");
    }

    /// <summary>A <c>response_format=text</c> answer is the transcript itself, not JSON.</summary>
    [Fact]
    public async Task A_plain_text_body_is_the_transcript()
    {
        _responseBody = "  hello there  ";

        var outcome = await Client(TranscriptionProvider.OpenAiCompatible, "whisper-1")
            .RecognizeAsync(Audio(out _), "voice-1.mp3", "audio/mpeg");

        outcome.Succeeded.ShouldBeTrue();
        outcome.Result!.Text.ShouldBe("hello there");
    }

    /// <summary>
    /// Without a key nothing is sent at all: the handler refuses with <c>TRANSCRIPTION_FAILED</c> up front
    /// rather than queueing work that would sit pending until every client times out.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_client_is_disabled_and_sends_nothing()
    {
        var client = Client(TranscriptionProvider.Deepgram, "nova-3", apiKey: string.Empty);

        client.IsEnabled.ShouldBeFalse();

        var outcome = await client.RecognizeAsync(Audio(out _), "voice-1.ogg", "audio/ogg");

        outcome.Succeeded.ShouldBeFalse();
        outcome.Retryable.ShouldBeFalse();
        _rawBodies.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------- harness

    private const string ApiKey = "test-key";

    private SpeechRecognitionClient Client(TranscriptionProvider provider, string model,
        string apiKey = ApiKey, bool detectLanguage = true, bool smartFormat = true)
    {
        var options = new MyTelegramMessengerServerOptions
        {
            Transcription = new TranscriptionConfig
            {
                Enabled = true,
                Provider = provider,
                BaseUrl = _baseUrl,
                ApiKey = apiKey,
                Model = model,
                DetectLanguage = detectLanguage,
                SmartFormat = smartFormat,
                TimeoutSeconds = 10
            }
        };

        return new SpeechRecognitionClient(
            new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(options),
            NullLogger<SpeechRecognitionClient>.Instance);
    }

    private static string Audio(out byte[] bytes)
    {
        bytes = [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0xFF];
        var path = Path.Combine(Path.GetTempPath(), $"transcribe-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            var raw = buffer.ToArray();

            _rawBodies.Add(raw);
            _bodies.Add(Encoding.UTF8.GetString(raw));
            _authorizations.Add(context.Request.Headers["Authorization"]);
            _contentTypes.Add(context.Request.ContentType);
            _paths.Add(context.Request.Url?.PathAndQuery ?? string.Empty);

            var payload = Encoding.UTF8.GetBytes(_responseBody);
            context.Response.StatusCode = _statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    }
}

/// <summary>An <see cref="IOptionsMonitor{T}"/> over one fixed value.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
