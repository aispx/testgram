using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ResetPasswordHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestResetPassword, MyTelegram.Schema.Account.IResetPasswordResult>
{
    protected override async Task<MyTelegram.Schema.Account.IResetPasswordResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestResetPassword obj)
    {
        var resetState = await twoFactorService.GetPasswordResetStateAsync(input.UserId);

        if (resetState.HasValue)
        {
            var untilDate = resetState.Value.AddDays(7);
            if (untilDate <= DateTime.UtcNow)
            {
                await twoFactorService.RemovePasswordAsync(input.UserId);
                await twoFactorService.ClearPasswordResetStateAsync(input.UserId);
                return new MyTelegram.Schema.Account.TResetPasswordOk();
            }

            return new MyTelegram.Schema.Account.TResetPasswordRequestedWait
            {
                UntilDate = (int)new DateTimeOffset(untilDate).ToUnixTimeSeconds()
            };
        }

        var retryAt = await twoFactorService.GetPasswordResetRetryAtAsync(input.UserId);
        if (retryAt.HasValue && retryAt.Value > DateTime.UtcNow)
        {
            return new MyTelegram.Schema.Account.TResetPasswordFailedWait
            {
                RetryDate = (int)new DateTimeOffset(retryAt.Value).ToUnixTimeSeconds()
            };
        }

        await twoFactorService.StartPasswordResetAsync(input.UserId);
        return new MyTelegram.Schema.Account.TResetPasswordRequestedWait
        {
            UntilDate = (int)DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()
        };
    }
}
