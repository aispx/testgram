using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class CancelPasswordEmailHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestCancelPasswordEmail, MyTelegram.Schema.IBool>
{
    protected override async Task<MyTelegram.Schema.IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestCancelPasswordEmail obj)
    {
        var hasPendingCode = await twoFactorService.HasPendingRecoveryEmailCodeAsync(input.UserId);
        if (!hasPendingCode)
        {
            RpcErrors.RpcErrors400.EmailHashExpired.ThrowRpcError();
        }

        await twoFactorService.CancelRecoveryEmailAsync(input.UserId);
        return new MyTelegram.Schema.TBoolTrue();
    }
}
