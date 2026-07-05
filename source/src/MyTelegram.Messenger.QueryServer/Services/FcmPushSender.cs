using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Sends push notifications through Firebase Cloud Messaging (FCM HTTP v1 API) for devices
/// registered with token_type = 2. The payload is delivered as a data message
/// (<c>{ "p": &lt;base64url-encrypted-payload&gt; }</c>), which the client's
/// <c>GcmPushListenerService</c> hands to <c>PushListenerController.processRemoteMessage</c>.
/// <para>See https://corefork.telegram.org/api/push-updates and
/// https://firebase.google.com/docs/cloud-messaging/http-server-ref .</para>
/// </summary>
public class FcmPushSender(IOptions<MyTelegramMessengerServerOptions> options, ILogger<FcmPushSender> logger)
    : IPushFcmSender, ITransientDependency
{
    private const string FcmScope = "https://www.googleapis.com/auth/firebase.messaging";

    private static readonly HttpClient Http = new();

    /// <summary>Cached OAuth2 access token and its absolute expiry time.</summary>
    private static FirebaseServiceAccount? _account;
    private static string? _projectId;
    private static (string Token, DateTime ExpiresAt) _cachedToken;
    private static readonly SemaphoreSlim TokenGate = new(1, 1);

    public async Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
    {
        var cfg = options.Value.Push.Fcm;
        if (!cfg.Enabled)
        {
            return PushSendOutcome.Delivered;
        }

        var account = EnsureAccount(cfg.ServiceAccountJson);
        if (account == null)
        {
            logger.LogWarning("FCM sender has no valid service account; skip push to token={Token}", Mask(device.Token));
            return PushSendOutcome.TransientFailure;
        }

        var accessToken = await GetAccessTokenAsync(account);
        var projectId = account.ProjectId ?? _projectId;
        if (string.IsNullOrEmpty(projectId))
        {
            logger.LogWarning("FCM service account JSON is missing project_id; skip push");
            return PushSendOutcome.TransientFailure;
        }

        var url = $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send";

        // Data-only message: clients decrypt `p` themselves (matches GcmPushListenerService).
        var body = JsonSerializer.Serialize(new
        {
            message = new
            {
                token = device.Token,
                data = new Dictionary<string, string>
                {
                    ["p"] = base64Payload
                },
                android = new { priority = device.NoMuted ? "high" : "normal" }
            }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, cfg.PushTimeoutSec)));
        using var resp = await Http.SendAsync(req, cts.Token);
        var respText = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("FCM push failed: {Status} {Body} token={Token}",
                (int)resp.StatusCode, respText, Mask(device.Token));
            // 404 with an UNREGISTERED error code means FCM has dropped the token; signal the
            // caller to remove it. Any other non-2xx is a transient failure and never throws.
            var isUnregistered = (int)resp.StatusCode == 404
                && respText.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase);
            return isUnregistered
                ? PushSendOutcome.TokenInvalidated
                : PushSendOutcome.TransientFailure;
        }

        return PushSendOutcome.Delivered;
    }

    private static FirebaseServiceAccount? EnsureAccount(string serviceAccountJson)
    {
        var account = TryParseServiceAccount(serviceAccountJson);
        if (account != null)
        {
            _account = account;
            _projectId = account.ProjectId;
        }
        else if (_account != null)
        {
            account = _account;
        }
        return account;
    }

    private static FirebaseServiceAccount? TryParseServiceAccount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var path = raw;
            string json = raw.TrimStart().StartsWith('{') ? raw :
                File.Exists(path) ? File.ReadAllText(path) : raw;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new FirebaseServiceAccount
            {
                ClientEmail = root.GetProperty("client_email").GetString() ?? "",
                PrivateKey = root.GetProperty("private_key").GetString() ?? "",
                ProjectId = root.TryGetProperty("project_id", out var pid) ? pid.GetString() ?? "" : ""
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> GetAccessTokenAsync(FirebaseServiceAccount account)
    {
        // Fast path: reuse cached token until 60s before expiry.
        if (_cachedToken.Token != null && _cachedToken.ExpiresAt > DateTime.UtcNow.AddSeconds(60))
        {
            return _cachedToken.Token;
        }

        await TokenGate.WaitAsync();
        try
        {
            if (_cachedToken.Token != null && _cachedToken.ExpiresAt > DateTime.UtcNow.AddSeconds(60))
            {
                return _cachedToken.Token;
            }

            var jwt = BuildServiceAccountJwt(account);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = jwt
                })
            };
            using var resp = await Http.SendAsync(req);
            var respText = await resp.Content.ReadAsStringAsync();
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(respText);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _cachedToken = (token, DateTime.UtcNow.AddSeconds(expiresIn));
            return token;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    /// <summary>
    /// Builds a Google OAuth2 service-account JWT assertion (RS256) per RFC 7519 / Google's spec.
    /// </summary>
    private static string BuildServiceAccountJwt(FirebaseServiceAccount account)
    {
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            iss = account.ClientEmail,
            scope = FcmScope,
            aud = "https://oauth2.googleapis.com/token",
            iat = now,
            exp = now + 3600
        });

        var headerB64 = PushPayloadEncryptor.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(header));
        var payloadB64 = PushPayloadEncryptor.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payload));
        var signingInput = System.Text.Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");

        using var rsa = RSA.Create();
        var keyPem = account.PrivateKey.Replace("\\n", "\n");
        rsa.ImportFromPem(keyPem);
        var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sigB64 = PushPayloadEncryptor.Base64UrlEncode(signature);
        return $"{headerB64}.{payloadB64}.{sigB64}";
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)] + "***";

    private sealed class FirebaseServiceAccount
    {
        public string ClientEmail { get; set; } = "";
        public string PrivateKey { get; set; } = "";
        public string? ProjectId { get; set; }
    }

    // Marker interface so the dispatcher can resolve all senders individually if needed.
}

public interface IPushFcmSender
{
    Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload);
}
