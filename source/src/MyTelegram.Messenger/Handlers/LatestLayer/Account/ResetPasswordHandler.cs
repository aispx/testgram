using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ResetPasswordHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestResetPassword, MyTelegram.Schema.Account.IResetPasswordResult>
{
    protected override async Task<MyTelegram.Schema.Account.IResetPasswordResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestResetPassword obj)
    {
        var resetState = await twoFactorService.GetPasswordResetStateAsync(input.UserId);
        
        if (resetState == null)
        {
            await twoFactorService.StartPasswordResetAsync(input.UserId);
            var untilDate = (int)(DateTimeOffset.UtcNow.Add(TimeSpan.FromDays(7)).ToUnixTimeSeconds());
            return new MyTelegram.Schema.Account.TResetPasswordRequestedWait
            {
                UntilDate = untilDate
            };
        }

        var daysSinceRequested = (DateTime.UtcNow - resetState.Value).TotalDays;
        
        if (daysSinceRequested >= 7)
        {
            await twoFactorService.RemovePasswordAsync(input.UserId);
            await twoFactorService.ClearPasswordResetStateAsync(input.UserId);
            return new MyTelegram.Schema.Account.TResetPasswordOk();
        }

        var retryDate = (int)(DateTimeOffset.UtcNow.Add(TimeSpan.FromDays(7) - TimeSpan.FromDays(daysSinceRequested)).ToUnixTimeSeconds());
        return new MyTelegram.Schema.Account.TResetPasswordFailedWait
        {
            RetryDate = retryDate
        };
    }
}
