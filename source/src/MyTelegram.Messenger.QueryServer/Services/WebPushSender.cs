using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Sends push notifications through the Web Push API (RFC 8030/8291) for devices registered with
/// token_type = 10. The token is the JSON object specified in
/// <see href="https://corefork.telegram.org/api/push-updates"/> (endpoint + keys.p256dh + keys.auth).
/// Messages are encrypted per RFC 8291 (aes128gcm) and signed with a VAPID JWT (RFC 8292).
/// </summary>
public class WebPushSender(IOptions<MyTelegramMessengerServerOptions> options, ILogger<WebPushSender> logger)
    : IPushWebPushSender, ITransientDependency
{
    private static readonly HttpClient Http = new();

    public async Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
    {
        var cfg = options.Value.Push.WebPush;
        if (!cfg.Enabled)
        {
            return PushSendOutcome.Delivered;
        }

        WebPushSubscription? sub;
        try
        {
            sub = JsonSerializer.Deserialize<WebPushSubscription>(device.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Invalid web-push token JSON for device {PermAuthKeyId}", device.PermAuthKeyId);
            return PushSendOutcome.TransientFailure;
        }
        if (sub is null || string.IsNullOrEmpty(sub.Endpoint) || sub.Keys is null)
        {
            logger.LogWarning("Web-push token is missing endpoint/keys for device {PermAuthKeyId}", device.PermAuthKeyId);
            return PushSendOutcome.TransientFailure;
        }

        try
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes(base64Payload);
            var encrypted = WebPushCrypto.Encrypt(sub.Keys.P256Dh, sub.Keys.Auth, plaintext);
            var vapidJwt = WebPushCrypto.BuildVapidJwt(cfg.VapidPrivateKey, cfg.VapidPublicKey, cfg.VapidSubject, sub.Endpoint);
            var publicKey = WebPushCrypto.PublicKeyFromPrivate(cfg.VapidPrivateKey);

            using var req = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint)
            {
                Content = new ByteArrayContent(encrypted)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");
            req.Headers.Authorization = new AuthenticationHeaderValue("vapid", $" t={vapidJwt}, k={publicKey}");
            req.Headers.TryAddWithoutValidation("TTL", "2419200");
            req.Headers.TryAddWithoutValidation("Urgency", device.NoMuted ? "high" : "normal");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, cfg.PushTimeoutSec)));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotModified)
            {
                logger.LogWarning("Web-push failed: {Status} {Body} endpoint={Endpoint}",
                    (int)resp.StatusCode, respText, sub.Endpoint);
                // 404 Not Found / 410 Gone => the subscription is expired or unsubscribed; signal
                // the caller to drop the token. Any other non-2xx is transient and never throws.
                var status = (int)resp.StatusCode;
                return status is 404 or 410
                    ? PushSendOutcome.TokenInvalidated
                    : PushSendOutcome.TransientFailure;
            }

            return PushSendOutcome.Delivered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web-push send error for endpoint={Endpoint}", sub.Endpoint);
            return PushSendOutcome.TransientFailure;
        }
    }
}

public interface IPushWebPushSender
{
    Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload);
}

internal sealed class WebPushSubscription
{
    public string Endpoint { get; set; } = "";
    public WebPushKeys? Keys { get; set; }
}

internal sealed class WebPushKeys
{
    public string P256Dh { get; set; } = "";
    public string Auth { get; set; } = "";
}
