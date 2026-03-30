using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ConfirmPasswordEmailHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestConfirmPasswordEmail, MyTelegram.Schema.IBool>
{
    protected override async Task<MyTelegram.Schema.IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestConfirmPasswordEmail obj)
    {
        var ok = await twoFactorService.ConfirmRecoveryEmailAsync(input.UserId, obj.Code);
        if (!ok) RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        return new MyTelegram.Schema.TBoolTrue();
    }
}
