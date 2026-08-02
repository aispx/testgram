using MyTelegram.Core;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns configuration parameters for Diffie-Hellman key generation. Can also return a random sequence of bytes of required length.
/// Possible errors
/// Code Type Description
/// 400 RANDOM_LENGTH_INVALID Random length invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDhConfig"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDhConfigHandler : RpcResultObjectHandler<RequestGetDhConfig, IDhConfig>
{
    private const int CurrentVersion = 2;

    protected override Task<IDhConfig> HandleCoreAsync(IRequestInput input, RequestGetDhConfig obj)
    {
        // Zero is accepted. Telegram-iOS issues getDhConfig(version: 0, random_length: 0) on every
        // secret-chat create/accept and on both PFS re-key steps (pfsRequestKey / pfsAcceptKey all start
        // with validatedEncryptionConfig), and wraps the call in an unbounded retry - so rejecting 0 makes
        // secret chats and re-keying hang silently on iOS forever instead of surfacing an error. TDLib
        // records that the real server ignores random_length entirely ("always returns 256 random bytes"),
        // so accepting 0 cannot break a client: Android asks for 256 and still gets 256.
        var randomLength = obj.RandomLength;
        if (randomLength < 0 || randomLength > 256)
        {
            RpcErrors.RpcErrors400.RandomLengthInvalid.ThrowRpcError();
        }

        // Clients mix these bytes into their local PRNG seed before generating secret-chat DH
        // exponents, so they must come from a CSPRNG - Random.Shared is xoshiro256**, whose state a
        // client can recover from a single 256-byte request.
        // https://corefork.telegram.org/mtproto/security_guidelines
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(randomLength);

        if (obj.Version == CurrentVersion)
        {
            return Task.FromResult<IDhConfig>(new TDhConfigNotModified
            {
                Random = random
            });
        }

        return Task.FromResult<IDhConfig>(new TDhConfig
        {
            G = 3,
            P = AuthConsts.Dh2048P,
            Version = CurrentVersion,
            Random = random
        });
    }
}
