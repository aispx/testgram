using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Web Push encryption (RFC 8291 "aes128gcm") and VAPID signing (RFC 8292).
/// All keys are base64url-encoded, per the Web Push API.
/// </summary>
internal static class WebPushCrypto
{
    private const int KeyIdLength = 65; // uncompressed P-256 public key (0x04 || X || Y)
    private const int TagSize = 16;     // AES-GCM authentication tag length
    private const int MaxRecordSize = 4096;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for a subscriber using their p256dh/auth secrets,
    /// producing the complete aes128gcm HTTP body (RFC 8291 with a single record).
    /// </summary>
    public static byte[] Encrypt(string p256DhB64, string authB64, byte[] plaintext)
    {
        var userPublicKey = Base64UrlDecode(p256DhB64);
        var userAuthSecret = Base64UrlDecode(authB64);

        // Ephemeral server key pair for this message.
        using var ecdh = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("nistP256"));
        var ephemeralParams = ecdh.ExportParameters(includePrivateParameters: true);
        var ephemeralPublicUncompressed = UncompressedPoint(ephemeralParams);

        // ECDH shared secret with the subscriber's public key.
        using var userEcdh = ECDiffieHellman.Create();
        userEcdh.ImportParameters(new ECParameters
        {
            Curve = ECCurve.CreateFromFriendlyName("nistP256"),
            Q = new ECPoint
            {
                X = userPublicKey[1..33].ToArray(),
                Y = userPublicKey[33..65].ToArray()
            }
        });
        var sharedSecret = ecdh.DeriveKeyMaterial(userEcdh.PublicKey);

        // IKM = HKDF-Expand(HKDF-Extract(auth_secret, sharedSecret),
        //                   "WebPush: info\0" || eph_pub || user_pub, 32)
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), ephemeralPublicUncompressed, userPublicKey);
        var ikm = HkdfExpand(HkdfExtract(userAuthSecret, sharedSecret), keyInfo, 32);

        // Content encryption key + nonce.
        var cek = HkdfExpand(ikm, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 16);
        var nonce = HkdfExpand(ikm, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 12);

        // Single record: plaintext || delimiter 0x02 (padded with zeros up to MaxRecordSize).
        var record = new byte[Math.Min(MaxRecordSize, plaintext.Length + 1)];
        Buffer.BlockCopy(plaintext, 0, record, 0, plaintext.Length);
        record[plaintext.Length] = 0x02; // last-record delimiter

        using var aesGcm = new AesGcm(cek);
        var ciphertext = new byte[record.Length];
        var tag = new byte[TagSize];
        aesGcm.Encrypt(nonce, record, ciphertext, tag);

        // Header: salt(16) || rs(4 big-endian) || idlen(1) || keyid(ephemeral public)
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var header = new byte[16 + 4 + 1 + KeyIdLength];
        Buffer.BlockCopy(salt, 0, header, 0, 16);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), (uint)MaxRecordSize);
        header[20] = (byte)KeyIdLength;
        Buffer.BlockCopy(ephemeralPublicUncompressed, 0, header, 21, KeyIdLength);

        return Concat(header, ciphertext, tag);
    }

    /// <summary>Builds the VAPID ES256 JWT (Authorization: vapid t=...).</summary>
    public static string BuildVapidJwt(string privateKeyB64, string publicKeyB64, string subject, string endpoint)
    {
        var header = JsonSerializer.Serialize(new { typ = "JWT", alg = "ES256" });
        var origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            aud = origin,
            exp = now + 12 * 3600,
            sub = subject
        });

        var headerB64 = PushPayloadEncryptor.Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadB64 = PushPayloadEncryptor.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Base64UrlDecode(privateKeyB64), out _);
        var rawSig = ecdsa.SignData(signingInput, HashAlgorithmName.SHA256);
        var joseSig = EcdsaDerToJose(rawSig);
        var sigB64 = PushPayloadEncryptor.Base64UrlEncode(joseSig);

        return $"{headerB64}.{payloadB64}.{sigB64}";
    }

    /// <summary>Returns the uncompressed P-256 public key (base64url) for the given private key.</summary>
    public static string PublicKeyFromPrivate(string privateKeyB64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Base64UrlDecode(privateKeyB64), out _);
        var pub = ecdsa.ExportParameters(includePrivateParameters: false);
        return PushPayloadEncryptor.Base64UrlEncode(UncompressedPoint(pub));
    }

    private static byte[] UncompressedPoint(ECParameters p)
    {
        var bytes = new byte[KeyIdLength];
        bytes[0] = 0x04;
        p.Q.X!.CopyTo(bytes, 1);
        p.Q.Y!.CopyTo(bytes, 33);
        return bytes;
    }

    // --- HKDF (RFC 5869) ---
    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        using var hmac = new HMACSHA256(prk);
        var t = Array.Empty<byte>();
        var okm = new byte[length];
        var okmOffset = 0;
        var counter = 1;
        while (okmOffset < length)
        {
            var input = Concat(t, info, new[] { (byte)counter });
            t = hmac.ComputeHash(input);
            var toCopy = Math.Min(t.Length, length - okmOffset);
            Buffer.BlockCopy(t, 0, okm, okmOffset, toCopy);
            okmOffset += toCopy;
            counter++;
        }
        return okm;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }
        return result;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private static byte[] EcdsaDerToJose(byte[] der)
    {
        if (der.Length < 8 || der[0] != 0x30)
        {
            return der;
        }
        var r = ReadDerInt(der, 1, out var offset);
        var s = ReadDerInt(der, offset, out _);
        var raw = new byte[64];
        var rPadded = new byte[32];
        Buffer.BlockCopy(r, 0, rPadded, 32 - r.Length, r.Length);
        var sPadded = new byte[32];
        Buffer.BlockCopy(s, 0, sPadded, 32 - s.Length, s.Length);
        Buffer.BlockCopy(rPadded, 0, raw, 0, 32);
        Buffer.BlockCopy(sPadded, 0, raw, 32, 32);
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
        if (val.Length > 1 && val[0] == 0)
        {
            val = val[1..];
        }
        next = offset;
        return val;
    }
}
