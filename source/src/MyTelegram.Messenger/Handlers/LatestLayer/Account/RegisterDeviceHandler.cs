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

        // Ignore the caller-supplied other_uids entirely. It lists sibling accounts a multi-account
        // client claims share this device, but the client asserts them without proof, and honouring them
        // would let any caller register their own device as a recipient of another user's push payloads
        // (including plaintext message bodies). Multi-account routing is instead recovered safely by
        // scoping the device identity to the authenticated account (PushDeviceId.Create(token, userId)):
        // every account on the shared token registers its own row from its own session, and the
        // dispatcher routes by owner. No account can register a token on another account's behalf.
        var otherUids = new List<long>();

        var command = new RegisterDeviceCommand(PushDeviceId.Create(obj.Token, input.UserId), input.ToRequestInfo(), input.UserId, input.PermAuthKeyId, obj.TokenType, obj.Token, obj.NoMuted, obj.AppSandbox, obj.Secret, otherUids);
        await commandBus.PublishAsync(command);

        // Migrate away from the pre-account-scoped row for this token so its last owner does not keep
        // receiving a duplicate push. A no-op once the legacy row is gone.
        var legacyCleanup = new UnRegisterDeviceCommand(PushDeviceId.CreateLegacy(obj.Token), input.ToRequestInfo(), obj.TokenType, obj.Token, otherUids);
        await commandBus.PublishAsync(legacyCleanup);

        return new TBoolTrue();
    }
}