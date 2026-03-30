using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class DeclinePasswordResetHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestDeclinePasswordReset, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestDeclinePasswordReset obj)
    {
        var resetState = await twoFactorService.GetPasswordResetStateAsync(input.UserId);
        if (resetState == null)
        {
            RpcErrors.RpcErrors400.ResetRequestMissing.ThrowRpcError();
        }

        await twoFactorService.DeclinePasswordResetAsync(input.UserId);
        return new TBoolTrue();
    }
}
