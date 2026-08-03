using System.Security.Cryptography;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class RequestPasswordRecoveryHandler(ITwoFactorService twoFactorService, IEmailSender emailSender)
    : RpcResultObjectHandler<RequestRequestPasswordRecovery, IPasswordRecovery>
{
    protected override async Task<IPasswordRecovery> HandleCoreAsync(IRequestInput input, RequestRequestPasswordRecovery obj)
    {
        var passwordDoc = await twoFactorService.GetPasswordAsync(input.UserId);
        if (passwordDoc == null || string.IsNullOrEmpty(passwordDoc.RecoveryEmail))
        {
            RpcErrors.RpcErrors400.PasswordRecoveryNa.ThrowRpcError();
        }

        // The recovery code is the single factor that resets a 2FA password, so it must come from a CSPRNG.
        // Random.Shared is xoshiro256**: its state is recoverable from earlier observed outputs (e.g. codes
        // sent to an address the attacker controls), which would let the next victim's code be predicted.
        var recoveryCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        await twoFactorService.SetRecoveryEmailAsync(input.UserId, passwordDoc.RecoveryEmail, recoveryCode);
        await emailSender.SendVerificationCodeAsync(passwordDoc.RecoveryEmail, "Testgram Password Recovery", recoveryCode, "Your password recovery code is:");

        return new TPasswordRecovery { EmailPattern = twoFactorService.GetRecoveryEmailPattern(passwordDoc.RecoveryEmail) ?? passwordDoc.RecoveryEmail };
    }
}
