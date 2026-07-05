namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// Independent base64url codec used by the test oracle. This intentionally does NOT call the
/// production <c>PushPayloadEncryptor.Base64UrlEncode</c> so that round-trip property tests can use
/// it as a trustworthy reference for the wire format official clients expect (base64url, no padding).
/// </summary>
public static class Base64UrlReference
{
    /// <summary>base64url-encode without padding (RFC 4648 §5, '=' trimmed).</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// base64url-decode a (possibly unpadded) string back to bytes. Restores standard alphabet and
    /// padding before decoding so it is the exact inverse of <see cref="Encode"/>.
    /// </summary>
    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
