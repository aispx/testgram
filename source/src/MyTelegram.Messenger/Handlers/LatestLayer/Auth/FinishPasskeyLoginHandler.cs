using Microsoft.Extensions.Logging;
using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class FinishPasskeyLoginHandler(
    IPasskeyService passkeyService,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IEventBus eventBus,
    ILogger<FinishPasskeyLoginHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestFinishPasskeyLogin, MyTelegram.Schema.Auth.IAuthorization>
{
    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestFinishPasskeyLogin obj)
    {
        if (obj.Credential is not TInputPasskeyCredentialPublicKey cred || cred.Response is not TInputPasskeyResponseLogin response)
        {
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        PasskeyLoginResult loginResult;
        try
        {
            var clientDataJson = PasskeyService.DecodeClientDataJson(response.ClientData.Data);
            var clientDataRaw = PasskeyService.DecodeClientDataBytes(response.ClientData.Data);

            loginResult = await passkeyService.VerifyLoginAsync(
                cred.Id,
                cred.RawId,
                clientDataJson,
                clientDataRaw,
                response.AuthenticatorData.ToArray(),
                response.Signature.ToArray(),
                response.UserHandle);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Passkey login verification failed");
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        var passkey = loginResult.Passkey;
        var now = DateTime.UtcNow.ToTimestamp();
        await passkeyService.UpdateSignCountAsync(passkey.Id, loginResult.SignCount, now);

        var userReadModel = await userAppService.GetAsync(loginResult.UserId);
        if (userReadModel == null)
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();

        if (userReadModel!.HasPassword)
        {
            await eventBus.PublishAsync(new UserSignInSuccessEvent(input.ReqMsgId, input.AuthKeyId, input.PermAuthKeyId, userReadModel.UserId, PasswordState.WaitingForVerify, true));
            RpcErrors.RpcErrors401.SessionPasswordNeeded.ThrowRpcError();
            return null!;
        }

        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userReadModel.UserId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        await eventBus.PublishAsync(new UserSignInSuccessEvent(input.ReqMsgId, input.AuthKeyId, input.PermAuthKeyId, userReadModel.UserId, PasswordState.None));

        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        var user = userConverterService.ToUser(input, userReadModel, photos, layer: input.Layer);
        user.Self = true;
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
