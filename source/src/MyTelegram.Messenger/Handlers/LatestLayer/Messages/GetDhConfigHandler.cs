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
        var randomLength = obj.RandomLength;
        if (randomLength < 1 || randomLength > 256)
        {
            RpcErrors.RpcErrors400.RandomLengthInvalid.ThrowRpcError();
        }

        var random = new byte[randomLength];
        Random.Shared.NextBytes(random);

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
