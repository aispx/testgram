// Feature: push-updates, Property 38: Не-успешный HTTP-статус не выбрасывает исключение.
//
// For any non-success HTTP status returned by a provider, the sender records the status/body in the
// log and completes without an unhandled exception (Req 12.2). Of the three senders, WebPushSender
// is the only one that POSTs to a per-device endpoint taken from the device token (the FCM/APNS
// senders post to fixed Google/Apple hosts and require live OAuth/HTTP2 handshakes).
//
// Seam: the senders all use a `private static readonly HttpClient`, which is neither
// constructor-injectable nor (on .NET 10) reassignable via reflection (initonly statics are sealed
// after type init). So this property drives the REAL HTTP path of the production WebPushSender by
// pointing the device's web-push endpoint at a local HttpListener that returns an arbitrary status
// code in the 400-599 range (which includes the stale-token codes 404/410).
//
// The sender is wired with a genuine VAPID P-256 key and a genuine subscriber P-256 key so the
// RFC 8291 encryption and RFC 8292 VAPID JWT signing actually succeed and a real request reaches the
// listener. For every generated non-2xx status the sender must:
//   * never throw (the call returns normally), and
//   * return a defined PushSendOutcome — TokenInvalidated for 404/410, TransientFailure otherwise,
//     and never Delivered.
//
// Validates: Requirements 12.2

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public sealed class Property38_NonSuccessStatusNoThrowTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _endpoint;
    private readonly WebPushSender _sender;
    private readonly string _p256dh;
    private readonly string _auth;

    public Property38_NonSuccessStatusNoThrowTests()
    {
        // A genuine VAPID key pair so JWT signing / public-key derivation succeed inside the sender.
        using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var vapidPrivateKey = Base64UrlReference.Encode(vapid.ExportECPrivateKey());
        var vapidPublicKey = Base64UrlReference.Encode(UncompressedPoint(vapid.ExportParameters(false)));

        // A genuine subscriber key pair so RFC 8291 encryption succeeds.
        using var subscriber = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _p256dh = Base64UrlReference.Encode(UncompressedPoint(subscriber.ExportParameters(false)));
        _auth = Base64UrlReference.Encode(RandomNumberGenerator.GetBytes(16));

        // Local listener that replies with whatever status code the current iteration requests.
        var port = FreeTcpPort();
        var prefix = $"http://localhost:{port}/";
        _endpoint = prefix + "push";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        var options = Options.Create(new MyTelegramMessengerServerOptions
        {
            Push = new PushConfig
            {
                Enabled = true,
                WebPush = new PushConfig.WebPushConfig
                {
                    VapidPrivateKey = vapidPrivateKey,
                    VapidPublicKey = vapidPublicKey,
                    VapidSubject = "mailto:admin@example.com",
                    PushTimeoutSec = 5
                }
            }
        });
        _sender = new WebPushSender(options, NullLogger<WebPushSender>.Instance);
    }

    // Property 38: Не-успешный HTTP-статус не выбрасывает исключение
    // Validates: Requirements 12.2
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(HttpFailureStatusArbitrary) })]
    public void NonSuccess_status_never_throws_and_maps_to_a_defined_outcome(HttpFailureStatus status)
    {
        var token = BuildWebPushToken(_endpoint, _p256dh, _auth);
        var device = new FakePushDeviceReadModel
        {
            Id = token,
            Token = token,
            TokenType = PushTokenType.WebPush,
            UserId = 1,
            PermAuthKeyId = 1,
            NoMuted = true
        };

        // Serve exactly one request with the requested status while the sender is in flight. The
        // SendAsync call must return normally (no exception escapes) for every non-2xx status.
        var serve = ServeOnce(_listener, status.Code);
        var outcome = _sender
            .SendAsync(device, "cGF5bG9hZA") // arbitrary base64url payload
            .GetAwaiter().GetResult();
        serve.GetAwaiter().GetResult();

        var expected = status.Code is 404 or 410
            ? PushSendOutcome.TokenInvalidated
            : PushSendOutcome.TransientFailure;

        outcome.ShouldBe(expected, $"status={status.Code}");
        outcome.ShouldNotBe(PushSendOutcome.Delivered, $"status={status.Code}");
    }

    /// <summary>Accepts one pending request and replies with <paramref name="statusCode"/>.</summary>
    private static async Task ServeOnce(HttpListener listener, int statusCode)
    {
        var context = await listener.GetContextAsync();
        // Drain the request body so the client write completes cleanly.
        using (var _ = context.Request.InputStream) { }
        context.Response.StatusCode = statusCode;
        var body = Encoding.UTF8.GetBytes($"{{\"error\":\"status-{statusCode}\"}}");
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    /// <summary>
    /// WebPushSender deserializes the token with default (case-sensitive) System.Text.Json into a
    /// PascalCase WebPushSubscription { Endpoint, Keys { P256Dh, Auth } }, so the JSON property names
    /// must be PascalCase for the sender to reach its HTTP-send code path.
    /// </summary>
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

/// <summary>A non-success HTTP status code (400-599), including the stale-token codes 404/410.</summary>
public readonly record struct HttpFailureStatus(int Code);

public static class HttpFailureStatusArbitrary
{
    public static Arbitrary<HttpFailureStatus> Statuses() =>
        Arb.From(Gen.Choose(400, 599).Select(c => new HttpFailureStatus(c)));
}
