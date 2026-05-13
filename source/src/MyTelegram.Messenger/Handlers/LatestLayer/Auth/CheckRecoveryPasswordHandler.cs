namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

using MyTelegram.Messenger.Services.TwoFactor;

internal sealed class CheckRecoveryPasswordHandler(ITwoFactorService twoFactorService) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestCheckRecoveryPassword, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestCheckRecoveryPassword obj)
    {
        if (string.IsNullOrWhiteSpace(obj.Code))
        {
            RpcErrors.RpcErrors400.CodeEmpty.ThrowRpcError();
        }

        var result = await twoFactorService.CheckRecoveryCodeAsync(input.UserId, obj.Code);
        if (result == RecoveryCodeCheckResult.Ok)
        {
            return new TBoolTrue();
        }

        if (result == RecoveryCodeCheckResult.CodeInvalid)
        {
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }

        RpcErrors.RpcErrors400.PasswordRecoveryExpired.ThrowRpcError();
        return null!;
    }
}
