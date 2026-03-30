using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class CheckPasswordHandler(
    ITwoFactorService twoFactorService,
    IUserAppService userAppService,
    ILayeredService<IAuthorizationConverter> layeredService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IEventBus eventBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestCheckPassword, MyTelegram.Schema.Auth.IAuthorization>
{
    protected override async Task<MyTelegram.Schema.Auth.IAuthorization> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestCheckPassword obj)
    {
        if (obj.Password is not TInputCheckPasswordSRP srp)
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();

        srp = (TInputCheckPasswordSRP)obj.Password;
        var userId = await twoFactorService.GetUserIdBySrpIdAsync(srp.SrpId);
        if (userId == null)
            RpcErrors.RpcErrors400.SrpIdInvalid.ThrowRpcError();

        var ok = await twoFactorService.VerifySrpAsync(userId!.Value, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray());
        if (!ok)
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();

        var userReadModel = await userAppService.GetAsync(userId.Value);
        if (userReadModel == null)
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();

        await eventBus.PublishAsync(new BindUserIdToSessionEvent(userReadModel.UserId, input.AuthKeyId, input.PermAuthKeyId, input.AccessHashKeyId));
        await eventBus.PublishAsync(new UserSignInSuccessEvent(input.ReqMsgId, input.AuthKeyId, input.PermAuthKeyId, userReadModel.UserId, PasswordState.None));

        var photos = await photoAppService.GetPhotosAsync(userReadModel);
        var user = userConverterService.ToUser(input, userReadModel, photos, layer: input.Layer);
        user.Self = true;
        return layeredService.GetConverter(input.Layer).CreateAuthorization(user);
    }
}
