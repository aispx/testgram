using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class FinishPasskeyLoginHandler(
    IPasskeyService passkeyService,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IEventBus eventBus) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestFinishPasskeyLogin, MyTelegram.Schema.Auth.IAuthorization>
{
    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestFinishPasskeyLogin obj)
    {
        if (obj.Credential is not TInputPasskeyCredentialPublicKey cred)
        {
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        var response = (TInputPasskeyResponseLogin)cred.Response;
        var clientDataJson = PasskeyService.DecodeClientDataJson(response.ClientData.Data);
        // clientData.Data is raw JSON string (not base64), hash the UTF8 bytes
        var clientDataRaw = System.Text.Encoding.UTF8.GetBytes(clientDataJson);

        PasskeyDocument passkey;
        try
        {
            passkey = await passkeyService.VerifyLoginAsync(
                cred.Id, clientDataJson, clientDataRaw,
                response.AuthenticatorData.ToArray(),
                response.Signature.ToArray(),
                response.UserHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Passkey] VerifyLogin failed: {ex.Message}\n{ex.StackTrace}");
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        var now = DateTime.UtcNow.ToTimestamp();
        await passkeyService.UpdateSignCountAsync(passkey.Id, passkey.SignCount + 1, now);

        var userReadModel = await userAppService.GetAsync(passkey.UserId);
        if (userReadModel == null)
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();

        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userReadModel!.UserId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        await eventBus.PublishAsync(new UserSignInSuccessEvent(input.ReqMsgId, input.AuthKeyId, input.PermAuthKeyId, userReadModel.UserId, PasswordState.None));

        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        var user = userConverterService.ToUser(input, userReadModel, photos, layer: input.Layer);
        user.Self = true;
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
