using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Sends push notifications through Apple Push Notification service (APNs HTTP/2) for devices
/// registered with token_type = 1 (APNS) or 9 (APNS VoIP). Auth uses a provider JWT (ES256)
/// signed with the .p8 Auth Key.
/// <para>The MTProto-encrypted payload is placed in <c>aps.payload.p</c>; for VoIP pushes the
/// whole notification is a background push so the client's push kit receives it.</para>
/// </summary>
public class ApnsPushSender(IOptions<MyTelegramMessengerServerOptions> options, ILogger<ApnsPushSender> logger)
    : IPushApnsSender, ITransientDependency
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler())
    {
        DefaultRequestVersion = new Version(2, 0),
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
    };

    private static (string Token, DateTime ExpiresAt) _cachedProviderJwt;
    private static readonly SemaphoreSlim JwtGate = new(1, 1);

    public async Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
    {
        var cfg = options.Value.Push.Apns;
        if (!cfg.Enabled)
        {
            return PushSendOutcome.Delivered;
        }

        var isVoip = device.TokenType == PushTokenType.ApnsVoip;
        // Sandbox vs production host is chosen by the AppSandbox flag the client set at registration.
        var host = device.AppSandbox ? "api.development.push.apple.com" : "api.push.apple.com";
        var topic = !string.IsNullOrEmpty(cfg.BundleId) ? cfg.BundleId : device.Token;
        if (isVoip && !string.IsNullOrEmpty(cfg.BundleId))
        {
            topic = cfg.BundleId + ".voip";
        }

        var url = $"https://{host}/3/device/{device.Token}";

        // APNs body: a data-style push. `content-available` keeps it silent/encryptable; the
        // official client decrypts `p` on receipt. For VoIP, the payload is delivered to the
        // push kit via the top-level "p" key (PushKit background dictionary).
        object body;
        if (isVoip)
        {
            body = new Dictionary<string, object>
            {
                ["p"] = base64Payload
            };
        }
        else
        {
            body = new
            {
                aps = new
                {
                    @content_available = 1,
                    mutable_content = 1,
                    // Sound is optional; the client also reads it from the decrypted JSON.
                    sound = "default"
                },
                p = base64Payload
            };
        }

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Version = new Version(2, 0);
        req.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
        var providerJwt = await GetProviderJwtAsync(cfg);
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", providerJwt);
        req.Headers.TryAddWithoutValidation("apns-topic", topic);
        req.Headers.TryAddWithoutValidation("apns-push-type", isVoip ? "voip" : "background");
        req.Headers.TryAddWithoutValidation("apns-priority", device.NoMuted ? "10" : "5");
        req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, cfg.PushTimeoutSec)));
        using var resp = await Http.SendAsync(req, cts.Token);
        var respText = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("APNs push failed: {Status} {Body} sandbox={Sandbox} voip={Voip} token={Token}",
                (int)resp.StatusCode, respText, device.AppSandbox, isVoip, Mask(device.Token));
            // 410 Unregistered => the device token is no longer valid; signal the caller to drop it.
            // Any other non-2xx is a transient failure (logged above) and never throws.
            return (int)resp.StatusCode == 410
                ? PushSendOutcome.TokenInvalidated
                : PushSendOutcome.TransientFailure;
        }

        return PushSendOutcome.Delivered;
    }

    /// <summary>Provider JWT valid up to 1h; cache and refresh ~5min before expiry.</summary>
    private async Task<string> GetProviderJwtAsync(PushConfig.ApnsConfig cfg)
    {
        if (_cachedProviderJwt.Token != null && _cachedProviderJwt.ExpiresAt > DateTime.UtcNow.AddMinutes(5))
        {
            return _cachedProviderJwt.Token;
        }

        await JwtGate.WaitAsync();
        try
        {
            if (_cachedProviderJwt.Token != null && _cachedProviderJwt.ExpiresAt > DateTime.UtcNow.AddMinutes(5))
            {
                return _cachedProviderJwt.Token;
            }

            var jwt = BuildProviderJwt(cfg);
            _cachedProviderJwt = (jwt, DateTime.UtcNow.AddMinutes(50));
            return jwt;
        }
        finally
        {
            JwtGate.Release();
        }
    }

    private static string BuildProviderJwt(PushConfig.ApnsConfig cfg)
    {
        var header = JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = cfg.KeyId });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            iss = cfg.TeamId,
            iat = now
        });

        var headerB64 = PushPayloadEncryptor.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(header));
        var payloadB64 = PushPayloadEncryptor.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payload));
        var signingInput = System.Text.Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");

        using var ecdsa = ECDsa.Create();
        var keyPem = cfg.AuthKeyP8.Replace("\\n", "\n");
        // APNs Auth Keys are P-256. Accept both raw base64 and PEM-wrapped forms.
        if (keyPem.Contains("BEGIN PRIVATE KEY"))
        {
            ecdsa.ImportFromPem(keyPem);
        }
        else
        {
            ecdsa.ImportECPrivateKey(Convert.FromBase64String(cfg.AuthKeyP8.Trim()), out _);
        }
        var rawSig = ecdsa.SignData(signingInput, HashAlgorithmName.SHA256);
        // Convert DER-encoded ECDSA signature to raw r||s (JOSE ES256 format).
        var joseSig = EcdsaDerToJose(rawSig);

        var sigB64 = PushPayloadEncryptor.Base64UrlEncode(joseSig);
        return $"{headerB64}.{payloadB64}.{sigB64}";
    }

    /// <summary>Converts an ASN.1 DER ECDSA signature to the concatenated r||s JOSE form.</summary>
    private static byte[] EcdsaDerToJose(byte[] der)
    {
        if (der.Length < 8 || der[0] != 0x30)
        {
            return der; // already raw, return as-is
        }
        var r = ReadDerInt(der, 1, out var offset);
        var s = ReadDerInt(der, offset, out _);
        var raw = new byte[64];
        r.CopyTo(raw, 32 - r.Length);
        s.CopyTo(raw, 64 - s.Length);
        return raw;
    }

    private static byte[] ReadDerInt(byte[] data, int offset, out int next)
    {
        if (data[offset] != 0x02)
        {
            throw new FormatException("Invalid DER ECDSA signature");
        }
        offset++;
        var len = data[offset];
        offset++;
        var val = new byte[len];
        Buffer.BlockCopy(data, offset, val, 0, len);
        offset += len;
        // Strip leading zero padding (sign byte) per DER.
        if (val.Length > 1 && val[0] == 0)
        {
            val = val[1..];
        }
        next = offset;
        return val;
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)] + "***";
}

public interface IPushApnsSender
{
    Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload);
}
