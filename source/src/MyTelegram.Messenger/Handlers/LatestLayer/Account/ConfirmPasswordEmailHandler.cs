using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ConfirmPasswordEmailHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestConfirmPasswordEmail, MyTelegram.Schema.IBool>
{
    protected override async Task<MyTelegram.Schema.IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestConfirmPasswordEmail obj)
    {
        var result = await twoFactorService.CheckRecoveryCodeAsync(input.UserId, obj.Code);
        switch (result)
        {
            case RecoveryCodeCheckResult.ExpiredOrMissing:
                RpcErrors.RpcErrors400.EmailHashExpired.ThrowRpcError();
                break;
            case RecoveryCodeCheckResult.CodeInvalid:
                RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
                break;
        }

        await twoFactorService.ConfirmRecoveryEmailAsync(input.UserId, obj.Code);
        return new MyTelegram.Schema.TBoolTrue();
    }
}
