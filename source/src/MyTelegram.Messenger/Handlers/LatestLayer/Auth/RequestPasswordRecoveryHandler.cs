using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class RequestPasswordRecoveryHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<RequestRequestPasswordRecovery, IPasswordRecovery>
{
    protected override async Task<IPasswordRecovery> HandleCoreAsync(IRequestInput input, RequestRequestPasswordRecovery obj)
    {
        var passwordDoc = await twoFactorService.GetPasswordAsync(input.UserId);
        if (passwordDoc == null || string.IsNullOrEmpty(passwordDoc.RecoveryEmail))
        {
            RpcErrors.RpcErrors400.PasswordRecoveryNa.ThrowRpcError();
        }

        var recoveryCode = Random.Shared.Next(100000, 999999).ToString();
        await twoFactorService.SetRecoveryEmailAsync(input.UserId, passwordDoc.RecoveryEmail, recoveryCode);

        return new TPasswordRecovery { EmailPattern = twoFactorService.GetRecoveryEmailPattern(passwordDoc.RecoveryEmail) ?? passwordDoc.RecoveryEmail };
    }
}
