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

        var email = passwordDoc.RecoveryEmail;
        var atIndex = email.IndexOf('@');
        var maskedEmail = atIndex > 0 ? $"{email[0]}{new string('*', atIndex - 1)}@{email[(atIndex + 1)..]}" : email;

        return new TPasswordRecovery { EmailPattern = maskedEmail };
    }
}
