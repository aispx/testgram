using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// A self-contained, independent reference implementation of the MTProto v2 push-payload encryption
/// scheme used by official Telegram clients (see https://corefork.telegram.org/api/push-updates).
/// <para>
/// This is deliberately written from scratch (it does NOT reuse the production
/// <c>PushPayloadEncryptor</c> / <c>MtpHelper</c> / <c>AesHelper</c>) so it can serve as a trustworthy
/// oracle in round-trip property tests: the server encrypts with the production code and the test
/// decrypts with this reference; if they agree, the wire format matches what the Android client does.
/// </para>
/// <para>
/// Wire format:
/// <c>[auth_key_id(8)][msg_key(16)][aes_ige( [int32_le len][json][random padding -> 16] )]</c>.
/// </para>
/// <list type="bullet">
///   <item><c>auth_key_id = SHA1(secret)[12..20]</c> (little-endian int64).</item>
///   <item><c>msg_key = SHA256(secret[96..128] + plaintext)[8..24]</c> (the <c>x = 8</c> "to server" fragment).</item>
///   <item>AES-IGE key/iv derived per MTProto v2 with <c>x = 8</c>.</item>
/// </list>
/// </summary>
public static class MtProtoV2ReferenceCrypto
{
    public const int SecretLength = 256;

    /// <summary>The result of decrypting a push wire payload.</summary>
    /// <param name="AuthKeyId">The auth_key_id read from the first 8 bytes.</param>
    /// <param name="MsgKey">The 16-byte msg_key read from bytes [8..24].</param>
    /// <param name="DeclaredLength">The int32 little-endian length prefix of the inner buffer.</param>
    /// <param name="Json">The recovered JSON payload string.</param>
    /// <param name="MsgKeyValid">Whether the recomputed msg_key matches the one on the wire.</param>
    /// <param name="AuthKeyIdValid">Whether the recomputed auth_key_id matches the one on the wire.</param>
    public sealed record DecryptResult(
        long AuthKeyId,
        byte[] MsgKey,
        int DeclaredLength,
        string Json,
        bool MsgKeyValid,
        bool AuthKeyIdValid);

    /// <summary>Computes <c>auth_key_id = SHA1(secret)[12..20]</c> as a little-endian int64.</summary>
    public static long ComputeAuthKeyId(ReadOnlySpan<byte> secret)
    {
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(secret, hash);
        return BinaryPrimitives.ReadInt64LittleEndian(hash.Slice(12));
    }

    /// <summary>
    /// Computes the MTProto v2 msg_key for a "to server" message:
    /// <c>SHA256(secret[88+x .. 88+x+32] + plaintext)[8..24]</c> with <c>x = 8</c>.
    /// </summary>
    public static byte[] ComputeMsgKey(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> plaintext)
    {
        const int x = 8;
        var buffer = new byte[32 + plaintext.Length];
        secret.Slice(88 + x, 32).CopyTo(buffer);
        plaintext.CopyTo(buffer.AsSpan(32));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer, hash);

        return hash.Slice(8, 16).ToArray();
    }

    /// <summary>
    /// Derives the 32-byte AES key and 32-byte AES-IGE iv per MTProto v2 with <c>x = 8</c>, exactly as
    /// the official clients do when reconstructing the keys to decrypt push payloads.
    /// </summary>
    public static (byte[] AesKey, byte[] AesIv) DeriveAesKeyIv(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> msgKey)
    {
        if (msgKey.Length != 16)
        {
            throw new ArgumentException("msg_key must be 16 bytes", nameof(msgKey));
        }

        const int x = 8;

        Span<byte> aSource = stackalloc byte[52];
        Span<byte> bSource = stackalloc byte[52];
        msgKey.CopyTo(aSource);
        secret.Slice(x, 36).CopyTo(aSource.Slice(16));
        secret.Slice(40 + x, 36).CopyTo(bSource);
        msgKey.CopyTo(bSource.Slice(36));

        Span<byte> sha256A = stackalloc byte[32];
        Span<byte> sha256B = stackalloc byte[32];
        SHA256.HashData(aSource, sha256A);
        SHA256.HashData(bSource, sha256B);

        var aesKey = new byte[32];
        sha256A.Slice(0, 8).CopyTo(aesKey);
        sha256B.Slice(8, 16).CopyTo(aesKey.AsSpan(8));
        sha256A.Slice(24, 8).CopyTo(aesKey.AsSpan(24));

        var aesIv = new byte[32];
        sha256B.Slice(0, 8).CopyTo(aesIv);
        sha256A.Slice(8, 16).CopyTo(aesIv.AsSpan(8));
        sha256B.Slice(24, 8).CopyTo(aesIv.AsSpan(24));

        return (aesKey, aesIv);
    }

    /// <summary>
    /// Encrypts a JSON payload into the push wire format. Provided so tests can build their own
    /// fixtures and verify the reference decryptor is the exact inverse of the reference encryptor.
    /// The padding can be supplied for determinism; when null, random padding to a 16-byte boundary
    /// is used (matching the production encryptor).
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> secret, string json, byte[]? padding = null)
    {
        if (secret.Length != SecretLength)
        {
            throw new ArgumentException($"secret must be {SecretLength} bytes", nameof(secret));
        }

        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var withLen = new byte[4 + jsonBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(withLen, jsonBytes.Length);
        jsonBytes.CopyTo(withLen.AsSpan(4));

        var remainder = withLen.Length % 16;
        var padLength = remainder == 0 ? 16 : 16 - remainder;
        var padded = new byte[withLen.Length + padLength];
        withLen.CopyTo(padded.AsSpan());
        if (padding is not null)
        {
            if (padding.Length < padLength)
            {
                throw new ArgumentException("supplied padding too short", nameof(padding));
            }
            padding.AsSpan(0, padLength).CopyTo(padded.AsSpan(withLen.Length));
        }
        else
        {
            RandomNumberGenerator.Fill(padded.AsSpan(withLen.Length));
        }

        var authKeyId = ComputeAuthKeyId(secret);
        var msgKey = ComputeMsgKey(secret, padded);
        var (aesKey, aesIv) = DeriveAesKeyIv(secret, msgKey);

        var encrypted = AesIgeEncrypt(padded, aesKey, aesIv);

        var output = new byte[24 + encrypted.Length];
        BinaryPrimitives.WriteInt64LittleEndian(output, authKeyId);
        msgKey.CopyTo(output.AsSpan(8));
        encrypted.CopyTo(output.AsSpan(24));
        return output;
    }

    /// <summary>
    /// Decrypts a push wire payload back to its JSON, recomputing and verifying msg_key/auth_key_id.
    /// </summary>
    public static DecryptResult Decrypt(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> wire)
    {
        if (secret.Length != SecretLength)
        {
            throw new ArgumentException($"secret must be {SecretLength} bytes", nameof(secret));
        }
        if (wire.Length < 24 || (wire.Length - 24) % 16 != 0)
        {
            throw new ArgumentException("wire payload has invalid length", nameof(wire));
        }

        var authKeyId = BinaryPrimitives.ReadInt64LittleEndian(wire.Slice(0, 8));
        var msgKey = wire.Slice(8, 16).ToArray();
        var encrypted = wire.Slice(24).ToArray();

        var (aesKey, aesIv) = DeriveAesKeyIv(secret, msgKey);
        var decrypted = AesIgeDecrypt(encrypted, aesKey, aesIv);

        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(decrypted);
        var jsonLength = Math.Clamp(declaredLength, 0, decrypted.Length - 4);
        var json = Encoding.UTF8.GetString(decrypted, 4, jsonLength);

        var recomputedMsgKey = ComputeMsgKey(secret, decrypted);
        var msgKeyValid = recomputedMsgKey.AsSpan().SequenceEqual(msgKey);
        var authKeyIdValid = ComputeAuthKeyId(secret) == authKeyId;

        return new DecryptResult(authKeyId, msgKey, declaredLength, json, msgKeyValid, authKeyIdValid);
    }

    /// <summary>
    /// Convenience helper: base64url-decode then decrypt (the production encryptor returns base64url).
    /// </summary>
    public static DecryptResult DecryptBase64Url(ReadOnlySpan<byte> secret, string base64UrlWire)
    {
        var wire = Base64UrlReference.Decode(base64UrlWire);
        return Decrypt(secret, wire);
    }

    // --- AES-IGE (standard MTProto block mode), implemented independently from production code. ---

    private static byte[] AesIgeEncrypt(byte[] source, byte[] key, byte[] iv)
    {
        if (source.Length % 16 != 0)
        {
            throw new ArgumentException("AES-IGE input must be a multiple of 16 bytes");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        var output = new byte[source.Length];
        Span<byte> cPrev = stackalloc byte[16];
        Span<byte> pPrev = stackalloc byte[16];
        iv.AsSpan(0, 16).CopyTo(cPrev);
        iv.AsSpan(16, 16).CopyTo(pPrev);

        Span<byte> block = stackalloc byte[16];
        var blockBuf = new byte[16];
        for (var offset = 0; offset < source.Length; offset += 16)
        {
            var p = source.AsSpan(offset, 16);
            for (var i = 0; i < 16; i++)
            {
                block[i] = (byte)(p[i] ^ cPrev[i]);
            }
            block.CopyTo(blockBuf);
            encryptor.TransformBlock(blockBuf, 0, 16, blockBuf, 0);
            var c = output.AsSpan(offset, 16);
            for (var i = 0; i < 16; i++)
            {
                c[i] = (byte)(blockBuf[i] ^ pPrev[i]);
            }
            c.CopyTo(cPrev);
            p.CopyTo(pPrev);
        }

        return output;
    }

    private static byte[] AesIgeDecrypt(byte[] source, byte[] key, byte[] iv)
    {
        if (source.Length % 16 != 0)
        {
            throw new ArgumentException("AES-IGE input must be a multiple of 16 bytes");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();

        var output = new byte[source.Length];
        Span<byte> cPrev = stackalloc byte[16];
        Span<byte> pPrev = stackalloc byte[16];
        iv.AsSpan(0, 16).CopyTo(cPrev);
        iv.AsSpan(16, 16).CopyTo(pPrev);

        Span<byte> block = stackalloc byte[16];
        var blockBuf = new byte[16];
        for (var offset = 0; offset < source.Length; offset += 16)
        {
            var c = source.AsSpan(offset, 16);
            for (var i = 0; i < 16; i++)
            {
                block[i] = (byte)(c[i] ^ pPrev[i]);
            }
            block.CopyTo(blockBuf);
            decryptor.TransformBlock(blockBuf, 0, 16, blockBuf, 0);
            var p = output.AsSpan(offset, 16);
            for (var i = 0; i < 16; i++)
            {
                p[i] = (byte)(blockBuf[i] ^ cPrev[i]);
            }
            c.CopyTo(cPrev);
            p.CopyTo(pPrev);
        }

        return output;
    }
}
