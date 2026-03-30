using System.Security.Cryptography;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Schema.Auth;
using IAuthorization = MyTelegram.Schema.Auth.IAuthorization;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class RecoverPasswordHandler(
    ITwoFactorService twoFactorService,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IEventBus eventBus)
    : RpcResultObjectHandler<RequestRecoverPassword, IAuthorization>
{
    protected override async Task<IAuthorization> HandleCoreAsync(IRequestInput input, RequestRecoverPassword obj)
    {
        if (string.IsNullOrEmpty(obj.Code))
            RpcErrors.RpcErrors400.CodeEmpty.ThrowRpcError();

        var passwordDoc = await twoFactorService.GetPasswordAsync(input.UserId);
        if (passwordDoc == null)
            RpcErrors.RpcErrors400.PasswordEmpty.ThrowRpcError();

        var isCodeValid = await twoFactorService.ConfirmRecoveryEmailAsync(input.UserId, obj.Code);
        if (!isCodeValid)
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();

        var newSettings = obj.NewSettings as TPasswordInputSettings;
        if (newSettings != null)
        {
            if (newSettings.NewAlgo is TPasswordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow algo
                && newSettings.NewPasswordHash is { Length: > 0 })
            {
                await twoFactorService.SetPasswordAsync(
                    input.UserId,
                    algo.Salt1,
                    algo.Salt2,
                    newSettings.NewPasswordHash,
                    newSettings.Hint);

                if (!string.IsNullOrEmpty(newSettings.Email))
                {
                    var code = RandomNumberGenerator.GetBytes(4);
                    var codeString = BitConverter.ToString(code).Replace("-", "").Substring(0, 6);
                    await twoFactorService.SetRecoveryEmailAsync(input.UserId, newSettings.Email, codeString);
                }
            }
            else if (newSettings.NewAlgo is TPasswordKdfAlgoUnknown || newSettings.NewPasswordHash == null || newSettings.NewPasswordHash.Length == 0)
            {
                await twoFactorService.RemovePasswordAsync(input.UserId);
            }
        }
        else
        {
            await twoFactorService.RemovePasswordAsync(input.UserId);
        }

        var userReadModel = await userAppService.GetAsync(input.UserId);
        if (userReadModel == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userReadModel.UserId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        await eventBus.PublishAsync(new UserSignInSuccessEvent(input.ReqMsgId, input.AuthKeyId, input.PermAuthKeyId, userReadModel.UserId, PasswordState.None));

        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        var user = userConverterService.ToUser(input, userReadModel, photos, layer: input.Layer);
        user.Self = true;
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
