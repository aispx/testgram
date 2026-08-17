// Feature: push-updates, EXAMPLE 11.5: secrets never reach the logs.
//
// Requirement 11.5: THE Server SHALL NOT write the Push_Secret value or the provider private
// keys (.p8, VAPID private key, service account private key) into the logs.
//
// This is an example-based (unit) test, not a property. It drives the realistically reachable
// logging paths of the push senders / encryptor with DISTINCTIVE MARKER secret/private-key values,
// captures every log line via an in-memory ILogger, and asserts that none of the marker secrets or
// private keys ever appear in the captured output (while non-secret data such as the endpoint or a
// masked token is allowed to appear).
//
// Paths exercised:
//   1. WebPushSender real HTTP path -> non-2xx (500) and stale-token (410) responses served by a
//      local HttpListener (the same seam Property38 uses). This drives the production
//      `logger.LogWarning("Web-push failed: {Status} {Body} endpoint={Endpoint}", ...)` path. The
//      sender is configured with a MARKER VAPID private key, and the surrounding push config also
//      carries a MARKER .p8 and a MARKER service-account private key; the device itself carries a
//      256-byte MARKER push secret. None of those marker values may appear in the logs.
//   2. WebPushSender invalid-token-JSON warning path (`logger.LogWarning(ex, "Invalid web-push
//      token JSON ...")`) — confirms even exception-carrying error logs stay free of marker secrets.
//   3. PushPayloadEncryptor.EncryptForDevice — this component takes NO ILogger, so there is no
//      logging surface at all. The test asserts it still produces (base64url) output and that the
//      raw marker push secret never appears in that output (it is encrypted, not echoed).
//
// Validates: Requirements 11.5

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTelegram.Core;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Services.Services;
using Shouldly;

namespace MyTelegram.Push.Tests;

public sealed class SecretsNotLoggedTests : IDisposable
{
    // ---- Distinctive marker secrets / private keys (must NEVER appear in any log) -------------

    /// <summary>256-byte push secret whose raw bytes spell a recognizable ASCII marker pattern.</summary>
    private static readonly byte[] MarkerPushSecret = BuildAsciiPatternSecret();

    private static readonly string MarkerPushSecretText = Encoding.ASCII.GetString(MarkerPushSecret);
    private static readonly string MarkerPushSecretBase64Url = Base64UrlReference.Encode(MarkerPushSecret);

    private const string MarkerP8 =
        "-----BEGIN PRIVATE KEY-----\nMARKER_P8_PRIVATE_KEY_DO_NOT_LOG_ABCDEFGHIJKLMNOP\n-----END PRIVATE KEY-----";

    private const string MarkerServiceAccountJson =
        "{\"type\":\"service_account\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\\nMARKER_SERVICE_ACCOUNT_PRIVATE_KEY_DO_NOT_LOG_QRSTUVWXYZ\\n-----END PRIVATE KEY-----\"}";

    private readonly HttpListener _listener;
    private readonly string _endpoint;
    private readonly string _markerVapidPrivateKey; // real EC private key (base64url) — also a secret
    private readonly MyTelegramMessengerServerOptions _options;
    private readonly string _p256dh;
    private readonly string _auth;

    public SecretsNotLoggedTests()
    {
        // Genuine VAPID key pair so the RFC 8292 JWT signing succeeds and a real request is sent.
        // The exported private key string is itself a secret we assert never leaks.
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _markerVapidPrivateKey = Base64UrlReference.Encode(vapid.ExportECPrivateKey());
        var vapidPublicKey = Base64UrlReference.Encode(UncompressedPoint(vapid.ExportParameters(false)));

        // Genuine subscriber key pair so the RFC 8291 (aes128gcm) encryption succeeds.
        using var subscriber = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _p256dh = Base64UrlReference.Encode(UncompressedPoint(subscriber.ExportParameters(false)));
        _auth = Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(16));

        var port = FreeTcpPort();
        var prefix = $"http://localhost:{port}/";
        _endpoint = prefix + "push";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        // Whole push config carries marker private keys for every provider so we prove the
        // WebPushSender never dumps neighbouring credentials either.
        _options = new MyTelegramMessengerServerOptions
        {
            Push = new PushConfig
            {
                Enabled = true,
                WebPush = new PushConfig.WebPushConfig
                {
                    VapidPrivateKey = _markerVapidPrivateKey,
                    VapidPublicKey = vapidPublicKey,
                    VapidSubject = "mailto:admin@example.com",
                    PushTimeoutSec = 5
                },
                Apns = new PushConfig.ApnsConfig
                {
                    AuthKeyP8 = MarkerP8,
                    KeyId = "ABC123DEFG",
                    TeamId = "TEAM123456",
                    BundleId = "com.example.app"
                },
                Fcm = new PushConfig.FcmConfig
                {
                    ServiceAccountJson = MarkerServiceAccountJson
                }
            }
        };
    }

    // ---- 1. WebPushSender non-2xx / stale-token logging path ----------------------------------

    [Theory]
    [InlineData(500)] // generic transient failure -> "Web-push failed" warning
    [InlineData(410)] // stale token -> still logs status/body, returns TokenInvalidated
    public void WebPushSender_non_success_status_does_not_log_any_secret(int statusCode)
    {
        var capture = new CapturingLogger<WebPushSender>();
        var sender = new WebPushSender(Options.Create(_options), capture);

        var token = BuildWebPushToken(_endpoint, _p256dh, _auth);
        var device = new FakePushDeviceReadModel
        {
            Id = token,
            Token = token,
            TokenType = PushTokenType.WebPush,
            UserId = 1,
            PermAuthKeyId = 1234,
            NoMuted = true,
            Secret = MarkerPushSecret // device push secret marker (sender must never log it)
        };

        var serve = ServeOnce(_listener, statusCode);
        var outcome = sender.SendAsync(device, "cGF5bG9hZA").GetAwaiter().GetResult();
        serve.GetAwaiter().GetResult();

        // The non-2xx path must have produced at least one log entry (so the assertion is meaningful).
        var log = capture.AllText;
        capture.Entries.ShouldNotBeEmpty();

        AssertNoSecretsLeaked(log);

        // Non-secret context is allowed (and expected) to appear in the warning.
        log.ShouldContain(statusCode.ToString());

        outcome.ShouldBe(statusCode is 404 or 410
            ? PushSendOutcome.TokenInvalidated
            : PushSendOutcome.TransientFailure);
    }

    // ---- 2. WebPushSender invalid-token-JSON warning path -------------------------------------

    [Fact]
    public void WebPushSender_invalid_token_json_warning_does_not_log_any_secret()
    {
        var capture = new CapturingLogger<WebPushSender>();
        var sender = new WebPushSender(Options.Create(_options), capture);

        // Malformed JSON (no secret marker inside the token) triggers the catch -> LogWarning(ex,...).
        var device = new FakePushDeviceReadModel
        {
            Id = "broken",
            Token = "{ this is not valid json",
            TokenType = PushTokenType.WebPush,
            UserId = 7,
            PermAuthKeyId = 99,
            Secret = MarkerPushSecret
        };

        var outcome = sender.SendAsync(device, "cGF5bG9hZA").GetAwaiter().GetResult();

        outcome.ShouldBe(PushSendOutcome.TransientFailure);
        capture.Entries.ShouldNotBeEmpty();
        AssertNoSecretsLeaked(capture.AllText);
    }

    // ---- 3. PushPayloadEncryptor (no logger) produces output without leaking the secret -------

    [Fact]
    public void Encryptor_produces_output_and_does_not_echo_the_marker_secret()
    {
        // PushPayloadEncryptor.EncryptForDevice takes NO ILogger: there is no logging surface to
        // leak through. We assert it still produces base64url output and that the raw marker secret
        // never appears in that output (it is MTProto-encrypted, not echoed back).
        IAuthKeyIdHelper authKeyIdHelper = new AuthKeyIdHelper();
        IMtpHelper mtpHelper = new MtpHelper(new AesHelper());

        var data = new PushData(
            PushNotificationTypes.MessageText,
            new[] { "Alice", "secret marker message" },
            UserId: 42,
            Custom: new PushNotificationCustomData { MsgId = 1, FromId = 2 },
            Sound: "default");

        var wire = PushPayloadEncryptor.EncryptForDevice(MarkerPushSecret, data, mtpHelper, authKeyIdHelper);

        wire.ShouldNotBeNullOrEmpty();
        // base64url, no padding / standard-alphabet chars.
        wire.ShouldNotContain("=");
        wire.ShouldNotContain("+");
        wire.ShouldNotContain("/");

        // The ciphertext must not contain the raw marker secret in any recognizable encoding.
        wire.ShouldNotContain(MarkerPushSecretText);
        wire.ShouldNotContain(MarkerPushSecretBase64Url);

        // Decoded output is the MTProto v2 wire format [auth_key_id(8)][msg_key(16)][aes_ige(...)],
        // and the raw secret bytes never appear verbatim inside it.
        var decoded = Base64UrlReference.Decode(wire);
        decoded.Length.ShouldBeGreaterThan(24);
        IndexOf(decoded, MarkerPushSecret).ShouldBe(-1);
    }

    // ---- shared assertion ---------------------------------------------------------------------

    private void AssertNoSecretsLeaked(string log)
    {
        log.ShouldNotContain(MarkerPushSecretText, Case.Sensitive);
        log.ShouldNotContain(MarkerPushSecretBase64Url, Case.Sensitive);
        log.ShouldNotContain(_markerVapidPrivateKey, Case.Sensitive);
        log.ShouldNotContain("MARKER_P8_PRIVATE_KEY_DO_NOT_LOG", Case.Sensitive);
        log.ShouldNotContain("MARKER_SERVICE_ACCOUNT_PRIVATE_KEY_DO_NOT_LOG", Case.Sensitive);
        log.ShouldNotContain("-----BEGIN PRIVATE KEY-----", Case.Sensitive);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static byte[] BuildAsciiPatternSecret()
    {
        const string pattern = "PUSH_SECRET_MARKER_DO_NOT_LOG_";
        var bytes = new byte[MtProtoV2ReferenceCrypto.SecretLength];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)pattern[i % pattern.Length];
        }
        return bytes;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static async Task ServeOnce(HttpListener listener, int statusCode)
    {
        var context = await listener.GetContextAsync();
        using (var _ = context.Request.InputStream) { }
        context.Response.StatusCode = statusCode;
        var body = Encoding.UTF8.GetBytes($"{{\"error\":\"status-{statusCode}\"}}");
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private static string BuildWebPushToken(string endpoint, string p256dh, string auth) =>
        JsonSerializer.Serialize(new
        {
            Endpoint = endpoint,
            Keys = new { P256Dh = p256dh, Auth = auth }
        });

    private static byte[] UncompressedPoint(ECParameters p)
    {
        var bytes = new byte[65];
        bytes[0] = 0x04;
        p.Q.X!.CopyTo(bytes, 1);
        p.Q.Y!.CopyTo(bytes, 33);
        return bytes;
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
        ((IDisposable)_listener).Dispose();
    }
}

/// <summary>
/// In-memory <see cref="ILogger{T}"/> that records every formatted log entry (including any
/// exception's full text) so tests can assert what did — and did not — reach the log.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries;

    public string AllText => string.Join("\n", _entries);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception is not null)
        {
            message += "\n" + exception;
        }
        _entries.Add($"[{logLevel}] {message}");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
