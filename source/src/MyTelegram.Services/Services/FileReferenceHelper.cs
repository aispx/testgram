using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MyTelegram.Services.Services;

/// <summary>
/// The <c>file_reference</c> of this server.
///
/// <para>Layout, 20 bytes: <c>ts(4B big-endian unix) || HMAC-SHA256(secret, type||id||ts)[0..15]</c>.
/// The timestamp travels in the clear so validation is one HMAC rather than a walk over every second of
/// the lifetime; the reference is still opaque to clients, which never parse it
/// (<a href="https://corefork.telegram.org/api/file-references">file references</a>).</para>
///
/// <para><b>Not bound to the caller.</b> <see cref="AccessHashHelper2"/> already binds media to a
/// session, and a reference that only one session could use would break every path where a media object
/// travels between accounts — a forward, an inline result, a bot reading a Passport scan. Deliberately
/// different from the access hash.</para>
///
/// <para><b>Not bound to the source either.</b> The official server encodes the source in the reference,
/// which is why its documentation says an expired one must be refetched from the place the media last
/// appeared. Here it is not: proving a caller still has access to a source is what the access hash
/// already does. Clients cope, because every one of them keys its refresh machinery off the error
/// prefix and not off the reference contents (tdlib <c>FileReferenceManager::is_file_reference_error</c>,
/// Android <c>FileRefController.isFileRefError</c>, tdesktop <c>ApiWrap::refreshFileReference</c>).</para>
/// </summary>
public sealed class FileReferenceHelper(
    IConfiguration configuration,
    ILogger<FileReferenceHelper> logger) : IFileReferenceHelper, ISingletonDependency
{
    /// <summary>Length of a reference this server issues. Anything else cannot be ours.</summary>
    public const int ReferenceLength = 20;

    private const int MacLength = 16;
    private const int PayloadLength = 13;

    /// <summary>
    /// A reference minted more than this far in the future can only come from a forged or corrupted
    /// value; the allowance covers clock drift between the hosts that mint and check.
    /// </summary>
    private const int FutureToleranceSeconds = 300;

    private byte[]? _secretKeyBytes;
    private int _ttlSeconds = -1;
    private FileReferenceMode? _mode;

    public FileReferenceMode Mode => _mode ??= ReadMode();

    /// <summary>
    /// <para>The issue timestamp is <b>quantised</b> to half the lifetime rather than taken from the
    /// clock. Two reasons, both of them load-bearing:</para>
    ///
    /// <para>A reference that changed on every response would change the bytes of responses whose hash
    /// clients quote back. <c>help.getAppConfig</c> is the sharp case: <c>emojies_sounds</c> carries a
    /// reference per document and <c>GetAppConfigHandler</c> folds those bytes into the config hash, so a
    /// per-call reference means the hash never repeats and <c>appConfigNotModified</c> can never
    /// fire.</para>
    ///
    /// <para>And it guarantees a floor on validity: whatever moment a client fetches in, the reference it
    /// receives still has at least half the lifetime left, so an ordinary session never meets
    /// <c>FILE_REFERENCE_EXPIRED</c> in the middle of a download.</para>
    /// </summary>
    public byte[] Create(AccessHashType type, long id)
    {
        return CreateAt(type, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    internal byte[] CreateAt(AccessHashType type, long id, long nowUnixSeconds)
    {
        var bucket = GetBucketSeconds();
        var issuedAt = (uint)(nowUnixSeconds - nowUnixSeconds % bucket);

        var reference = new byte[ReferenceLength];
        BinaryPrimitives.WriteUInt32BigEndian(reference.AsSpan(0, 4), issuedAt);

        Span<byte> mac = stackalloc byte[32];
        ComputeMac(type, id, issuedAt, mac);
        mac[..MacLength].CopyTo(reference.AsSpan(4));

        return reference;
    }

    public FileReferenceState Validate(ReadOnlySpan<byte> reference, AccessHashType type, long id)
    {
        return ValidateAt(reference, type, id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    internal FileReferenceState ValidateAt(ReadOnlySpan<byte> reference, AccessHashType type, long id,
        long nowUnixSeconds)
    {
        if (reference.IsEmpty)
        {
            return FileReferenceState.Empty;
        }

        if (reference.Length != ReferenceLength)
        {
            return FileReferenceState.Invalid;
        }

        var issuedAt = BinaryPrimitives.ReadUInt32BigEndian(reference[..4]);

        Span<byte> mac = stackalloc byte[32];
        ComputeMac(type, id, issuedAt, mac);

        if (!CryptographicOperations.FixedTimeEquals(mac[..MacLength], reference[4..]))
        {
            return FileReferenceState.Invalid;
        }

        if (issuedAt > nowUnixSeconds + FutureToleranceSeconds)
        {
            return FileReferenceState.Invalid;
        }

        return issuedAt + GetTtlSeconds() < nowUnixSeconds
            ? FileReferenceState.Expired
            : FileReferenceState.Valid;
    }

    public void Check(ReadOnlySpan<byte> reference, AccessHashType type, long id, int? index = null,
        bool isCover = false)
    {
        var mode = Mode;
        if (mode == FileReferenceMode.Off)
        {
            return;
        }

        var state = Validate(reference, type, id);
        if (state == FileReferenceState.Valid)
        {
            return;
        }

        if (mode == FileReferenceMode.LogOnly)
        {
            logger.LogWarning(
                "file_reference would have been refused: state={State}, type={Type}, id={Id}, length={Length}, index={Index}, isCover={IsCover}. Running in LogOnly mode, the request was allowed.",
                state, type, id, reference.Length, index, isCover);
            return;
        }

        Throw(state, index, isCover);
    }

    /// <summary>
    /// The error name for a state, in the spelling clients parse.
    ///
    /// <para>tdlib's <c>get_file_reference_error_source</c> reads the digits straight after the
    /// <c>FILE_REFERENCE_</c> prefix as the index into the failing vector, then looks for a
    /// <c>COVER_</c> prefix on what is left; Android's <c>getFileRefErrorIndex</c> only accepts an
    /// indexed name that also ends in <c>_EXPIRED</c>, and its <c>isFileRefErrorCover</c> looks for the
    /// <c>COVER_EXPIRED</c> suffix. So the four spellings below are the wire contract.</para>
    ///
    /// <para>The generated <c>RpcErrors</c> list carries only the four plain and two indexed forms;
    /// <c>FILE_REFERENCE_%d_EMPTY</c> (documented by <c>messages.sendMultiMedia</c>) and the
    /// <c>COVER_</c> family are composed here.</para>
    /// </summary>
    private static void Throw(FileReferenceState state, int? index, bool isCover)
    {
        if (!isCover)
        {
            switch (state)
            {
                case FileReferenceState.Empty when index == null:
                    RpcErrors.RpcErrors400.FileReferenceEmpty.ThrowRpcError();
                    break;
                case FileReferenceState.Expired when index == null:
                    RpcErrors.RpcErrors400.FileReferenceExpired.ThrowRpcError();
                    break;
                case FileReferenceState.Expired:
                    RpcErrors.RpcErrors400.FileReferenceXExpired.ThrowRpcError(index.Value);
                    break;
                case FileReferenceState.Invalid when index != null:
                    RpcErrors.RpcErrors400.FileReferenceXInvalid.ThrowRpcError(index.Value);
                    break;
                case FileReferenceState.Invalid:
                    RpcErrors.RpcErrors400.FileReferenceInvalid.ThrowRpcError();
                    break;
            }
        }

        var name = state switch
        {
            FileReferenceState.Empty => "EMPTY",
            FileReferenceState.Expired => "EXPIRED",
            _ => "INVALID"
        };

        var cover = isCover ? "COVER_" : string.Empty;
        var message = index == null
            ? $"FILE_REFERENCE_{cover}{name}"
            : $"FILE_REFERENCE_{index.Value}_{cover}{name}";

        new RpcError(400, message).ThrowRpcError();
    }

    private void ComputeMac(AccessHashType type, long id, uint issuedAt, Span<byte> destination)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(1, 8), id);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(9, 4), issuedAt);

        HMACSHA256.HashData(GetSecretKeyBytes(), payload, destination);
    }

    /// <summary>
    /// Falls back to the access hash secret so an existing deployment needs no new environment variable
    /// to start issuing real references. A deployment that wants the two rotated independently sets
    /// <c>App:FileReferences:SecretKey</c>.
    /// </summary>
    private byte[] GetSecretKeyBytes()
    {
        if (_secretKeyBytes != null)
        {
            return _secretKeyBytes;
        }

        var key = configuration.GetValue<string>("App:FileReferences:SecretKey");
        if (string.IsNullOrEmpty(key))
        {
            key = configuration.GetValue<string>("App:AccessHashSecretKey");
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                "Neither App:FileReferences:SecretKey nor App:AccessHashSecretKey is set, so no file reference can be signed");
        }

        return _secretKeyBytes = Encoding.UTF8.GetBytes(key);
    }

    private int GetTtlSeconds()
    {
        if (_ttlSeconds > 0)
        {
            return _ttlSeconds;
        }

        var hours = configuration.GetValue<int?>("App:FileReferences:TtlHours") ?? 48;
        if (hours < 1)
        {
            hours = 48;
        }

        return _ttlSeconds = hours * 3600;
    }

    private uint GetBucketSeconds()
    {
        return (uint)Math.Max(1, GetTtlSeconds() / 2);
    }

    private FileReferenceMode ReadMode()
    {
        var configured = configuration.GetValue<string>("App:FileReferences:Mode");

        return Enum.TryParse<FileReferenceMode>(configured, ignoreCase: true, out var mode)
            ? mode
            : FileReferenceMode.LogOnly;
    }
}
