using System.Security.Cryptography;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.Push;

/// <summary>
/// Validates the <c>token</c>/<c>token_type</c> pair supplied to <c>account.registerDevice</c>,
/// mirroring the error contract documented for
/// <a href="https://corefork.telegram.org/api/push-updates">Handling PUSH-notifications</a>.
/// <para>
/// Validation runs in the request handler before any command is published so an invalid request is
/// rejected with the correct RPC error and no device is created.
/// </para>
/// </summary>
public interface IPushTokenValidator
{
    /// <summary>
    /// Returns <c>null</c> when the token is valid, otherwise the RPC error that should be returned to
    /// the client (one of <c>TOKEN_EMPTY</c>, <c>TOKEN_TYPE_INVALID</c>, <c>WEBPUSH_TOKEN_INVALID</c>,
    /// <c>WEBPUSH_AUTH_INVALID</c>, <c>WEBPUSH_KEY_INVALID</c>).
    /// </summary>
    RpcError? Validate(int tokenType, string token);
}

public sealed class PushTokenValidator : IPushTokenValidator, ISingletonDependency
{
    /// <summary>
    /// Token types accepted by the server. Type 7 is used by Telegram Android's native internal
    /// push-session registration: the client sends the MTProto pushSessionId as the token.
    /// </summary>
    private static readonly HashSet<int> SupportedTokenTypes = [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13];

    /// <summary>Web Push token type: the token is a JSON subscription object.</summary>
    private const int WebPushTokenType = 10;

    /// <summary>Length in bytes of an uncompressed P-256 public key: <c>0x04 || X(32) || Y(32)</c>.</summary>
    private const int UncompressedP256KeyLength = 65;

    public RpcError? Validate(int tokenType, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return RpcErrors.RpcErrors400.TokenEmpty;
        }

        if (!SupportedTokenTypes.Contains(tokenType))
        {
            return RpcErrors.RpcErrors400.TokenTypeInvalid;
        }

        if (tokenType == WebPushTokenType)
        {
            return ValidateWebPushToken(token);
        }

        return null;
    }

    private static RpcError? ValidateWebPushToken(string token)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(token);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not parseable JSON => the required "endpoint" cannot be present.
            return RpcErrors.RpcErrors400.WebpushTokenInvalid;
        }

        // endpoint must be a non-empty string (Req 1.6).
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("endpoint", out var endpoint) ||
            endpoint.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(endpoint.GetString()))
        {
            return RpcErrors.RpcErrors400.WebpushTokenInvalid;
        }

        var hasKeys = root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object;

        // keys.auth must be present and a valid base64url string (Req 1.7).
        if (!hasKeys ||
            !keys.TryGetProperty("auth", out var auth) ||
            auth.ValueKind != JsonValueKind.String ||
            !TryDecodeBase64Url(auth.GetString(), out _))
        {
            return RpcErrors.RpcErrors400.WebpushAuthInvalid;
        }

        // keys.p256dh must be present, valid base64url, and a valid P-256 public key (Req 1.8).
        if (!keys.TryGetProperty("p256dh", out var p256dh) ||
            p256dh.ValueKind != JsonValueKind.String ||
            !TryDecodeBase64Url(p256dh.GetString(), out var p256dhBytes) ||
            !IsValidP256PublicKey(p256dhBytes))
        {
            return RpcErrors.RpcErrors400.WebpushKeyInvalid;
        }

        return null;
    }

    /// <summary>
    /// Attempts to decode a base64url string (RFC 4648 §5, no padding). Standard-base64 characters
    /// (<c>+</c>, <c>/</c>) and explicit padding (<c>=</c>) are rejected as not being base64url.
    /// </summary>
    private static bool TryDecodeBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains('+') || value.Contains('/') || value.Contains('='))
        {
            return false;
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="keyBytes"/> is an uncompressed P-256 public key whose point
    /// lies on the curve. Importing the parameters performs the on-curve validation.
    /// </summary>
    private static bool IsValidP256PublicKey(byte[] keyBytes)
    {
        if (keyBytes.Length != UncompressedP256KeyLength || keyBytes[0] != 0x04)
        {
            return false;
        }

        try
        {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportParameters(new ECParameters
            {
                Curve = ECCurve.CreateFromFriendlyName("nistP256"),
                Q = new ECPoint
                {
                    X = keyBytes[1..33],
                    Y = keyBytes[33..65]
                }
            });
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
