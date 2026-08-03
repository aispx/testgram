namespace MyTelegram.MTProto;

/// <summary>
///     Transport-level AES for the MTProto gateway. Only AES-CTR (obfuscation) belongs here; AES-IGE, which
///     the MTProto record layer uses, lives in <c>MyTelegram.Core.IAesHelper</c>. There used to be a second
///     IGE implementation on this interface whose decrypt path omitted the IV half-swap and therefore produced
///     wrong plaintext; it had no callers and was removed rather than duplicated.
/// </summary>
public interface IAesHelper
{
    void CtrEncrypt(ReadOnlySpan<byte> input, Span<byte> output, byte[] key, byte[] iv,
        ulong offset = 0);
}
