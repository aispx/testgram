using MyTelegram.Messenger.Services.Push;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Register device to receive <a href="https://corefork.telegram.org/api/push-updates">PUSH notifications</a>
/// Possible errors
/// Code Type Description
/// 400 TOKEN_EMPTY The specified token is empty.
/// 400 TOKEN_INVALID The provided token is invalid.
/// 400 TOKEN_TYPE_INVALID The specified token type is invalid.
/// 400 WEBPUSH_AUTH_INVALID The specified web push authentication secret is invalid.
/// 400 WEBPUSH_KEY_INVALID The specified web push elliptic curve Diffie-Hellman public key is invalid.
/// 400 WEBPUSH_TOKEN_INVALID The specified web push token is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.registerDevice"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RegisterDeviceHandler(ICommandBus commandBus, IPushTokenValidator pushTokenValidator) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestRegisterDevice, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestRegisterDevice obj)
    {
        // Validate the token/token_type pair before publishing the command so an invalid request is
        // rejected with the correct RPC error and no device is created (Req 1.2, 1.3, 1.5-1.8).
        var error = pushTokenValidator.Validate(obj.TokenType, obj.Token);
        if (error != null)
        {
            throw new RpcException(error.Value);
        }

        var command = new RegisterDeviceCommand(PushDeviceId.Create(obj.Token), input.ToRequestInfo(), input.UserId, input.PermAuthKeyId, obj.TokenType, obj.Token, obj.NoMuted, obj.AppSandbox, obj.Secret, obj.OtherUids.ToList());
        await commandBus.PublishAsync(command);
        return new TBoolTrue();
    }
}