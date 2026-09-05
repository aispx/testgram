using System.IO;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Translation;
using MyTelegram.Messenger.Tests.Transcription;

namespace MyTelegram.Messenger.Tests.Translation;

/// <summary>
/// Feature: the request <see cref="DeepLTranslationClient"/> puts on the wire, and what it makes of the
/// answer.
///
/// <para>The provider itself is the only unmeasurable part, so it is exercised against a local listener.
/// The shape is what DeepL actually takes, verified against the live API this session: form-urlencoded
/// with the texts as <b>repeated</b> <c>text</c> fields (which is what makes a twenty-message batch one
/// call), <c>Authorization: DeepL-Auth-Key …</c>, and <c>tag_handling=html</c> only when there is markup
/// to reposition.</para>
///
/// <para>The error mapping is the part a client sees. DeepL reports <c>456</c> for a spent monthly
/// character quota and <c>429</c> for a rate limit; both are <c>TRANSLATE_REQ_QUOTA_EXCEEDED</c>, the
/// error documented as "a temporary server-side lack of resources". A rejected key is <b>not</b> a
/// caller error and must not be reported as one.</para>
///
/// <para>A count mismatch is a failure rather than something to pass on: every client pairs the answer
/// with its request positionally, so a short vector silently mismaps translations onto the wrong
/// messages.</para>
/// </summary>
public class DeepLTranslationClientTests : IDisposable
{
    private const string ApiKey = "test-key:fx";

    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly List<string> _bodies = [];
    private readonly List<string?> _authorizations = [];
    private readonly List<string> _paths = [];

    private string _responseBody = "{\"translations\":[]}";
    private int _statusCode = 200;

    public DeepLTranslationClientTests()
    {
        var port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}/v2";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Close();
    }

    [Fact]
    public async Task The_texts_travel_as_repeated_form_fields_with_a_deepl_auth_header()
    {
        _responseBody = """{"translations":[{"text":"Привет"},{"text":"мир"}]}""";

        var outcome = await Client().TranslateAsync(["Hello", "world"], "RU", null, false);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Texts.ShouldBe(["Привет", "мир"]);

        _paths.ShouldHaveSingleItem().ShouldBe("/v2/translate");
        _authorizations.ShouldHaveSingleItem().ShouldBe($"DeepL-Auth-Key {ApiKey}");

        var form = Form(_bodies.ShouldHaveSingleItem());
        form.GetValues("text").ShouldBe(["Hello", "world"]);
        form["target_lang"].ShouldBe("RU");
        form["preserve_formatting"].ShouldBe("1");
    }

    /// <summary>
    /// <c>tag_handling</c> is what makes DeepL move a <c>&lt;span&gt;</c> to the translated words instead of
    /// leaving it at the original offset. Asking for it on plain text would make a literal <c>&lt;</c> in a
    /// message into markup the provider tries to balance.
    /// </summary>
    [Fact]
    public async Task Tag_handling_is_only_asked_for_when_there_is_markup()
    {
        _responseBody = """{"translations":[{"text":"x"}]}""";

        await Client().TranslateAsync(["plain"], "RU", null, false);
        Form(_bodies[0])["tag_handling"].ShouldBeNull();

        await Client().TranslateAsync(["<span id=\"0\">markup</span>"], "RU", null, true);
        Form(_bodies[1])["tag_handling"].ShouldBe("html");
    }

    /// <summary>
    /// DeepL 400s when formality is sent for a target that does not support it, and the tolerant
    /// <c>prefer_*</c> spelling is what keeps a supported target from failing on an unsupported variant.
    /// </summary>
    [Fact]
    public async Task Formality_is_sent_only_for_a_target_that_takes_one()
    {
        _responseBody = """{"translations":[{"text":"x"}]}""";

        await Client().TranslateAsync(["text"], "RU", "prefer_more", false);
        Form(_bodies[0])["formality"].ShouldBe("prefer_more");

        await Client().TranslateAsync(["text"], "UK", "prefer_more", false);
        Form(_bodies[1])["formality"].ShouldBeNull();
    }

    /// <summary>
    /// Both are "come back later" as far as a client is concerned, and both are what the documented
    /// <c>TRANSLATE_REQ_QUOTA_EXCEEDED</c> describes. 456 is DeepL's monthly character quota.
    /// </summary>
    [Theory]
    [InlineData(456)]
    [InlineData(429)]
    public async Task A_spent_quota_and_a_rate_limit_are_both_reported_as_quota_exceeded(int status)
    {
        _statusCode = status;
        _responseBody = """{"message":"Quota exceeded."}""";

        var outcome = await Client().TranslateAsync(["Hello"], "RU", null, false);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Failure.ShouldBe(TextTranslationFailure.QuotaExceeded);
    }

    /// <summary>
    /// A bad key is this deployment's problem, not the caller's, so it comes back as the generic
    /// failure — reporting it as a quota or an input error would send a client into the wrong recovery.
    /// </summary>
    [Theory]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task Everything_else_is_the_generic_failure(int status)
    {
        _statusCode = status;
        _responseBody = """{"message":"Authorization failure."}""";

        var outcome = await Client().TranslateAsync(["Hello"], "RU", null, false);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Failure.ShouldBe(TextTranslationFailure.Failed);
        outcome.Error.ShouldContain("Authorization failure.");
    }

    /// <summary>
    /// tdlib refuses a count mismatch outright ("Receive invalid number of results") and Android
    /// mismaps it silently, so a short answer must never be handed on.
    /// </summary>
    [Fact]
    public async Task A_short_answer_is_a_failure_rather_than_a_partial_result()
    {
        _responseBody = """{"translations":[{"text":"Привет"}]}""";

        var outcome = await Client().TranslateAsync(["Hello", "world"], "RU", null, false);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Failure.ShouldBe(TextTranslationFailure.Failed);
    }

    [Fact]
    public async Task An_unparseable_answer_is_a_failure()
    {
        _responseBody = "not json at all";

        var outcome = await Client().TranslateAsync(["Hello"], "RU", null, false);

        outcome.Succeeded.ShouldBeFalse();
        outcome.Failure.ShouldBe(TextTranslationFailure.Failed);
    }

    /// <summary>
    /// No key means no provider, and the caller turns that into <c>406 TRANSLATIONS_DISABLED</c> plus an
    /// <c>updateServiceNotification</c> rather than into fabricated text.
    /// </summary>
    [Fact]
    public async Task Without_a_key_the_client_is_disabled_and_asks_nothing()
    {
        var client = Client(apiKey: string.Empty);

        client.IsEnabled.ShouldBeFalse();

        var outcome = await client.TranslateAsync(["Hello"], "RU", null, false);

        outcome.Succeeded.ShouldBeFalse();
        _bodies.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_texts_means_no_request()
    {
        var outcome = await Client().TranslateAsync([], "RU", null, false);

        outcome.Succeeded.ShouldBeTrue();
        outcome.Texts.ShouldBeEmpty();
        _bodies.ShouldBeEmpty();
    }

    private DeepLTranslationClient Client(string? apiKey = null)
    {
        var options = new MyTelegramMessengerServerOptions
        {
            Translation = new TranslationConfig
            {
                Enabled = true,
                Provider = TextTranslationProvider.DeepL,
                BaseUrl = _baseUrl,
                ApiKey = apiKey ?? ApiKey,
                TimeoutSeconds = 10
            }
        };

        return new DeepLTranslationClient(new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(options),
            NullLogger<DeepLTranslationClient>.Instance);
    }

    private static System.Collections.Specialized.NameValueCollection Form(string body)
    {
        return HttpUtility.ParseQueryString(body, Encoding.UTF8);
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

            _bodies.Add(Encoding.UTF8.GetString(buffer.ToArray()));
            _authorizations.Add(context.Request.Headers["Authorization"]);
            _paths.Add(context.Request.Url?.AbsolutePath ?? string.Empty);

            var payload = Encoding.UTF8.GetBytes(_responseBody);
            context.Response.StatusCode = _statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        }
    }
}
