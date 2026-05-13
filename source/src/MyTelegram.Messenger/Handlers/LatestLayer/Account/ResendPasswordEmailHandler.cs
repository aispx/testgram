using System.Security.Cryptography;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ResendPasswordEmailHandler(ITwoFactorService twoFactorService, IEmailSender emailSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestResendPasswordEmail, MyTelegram.Schema.IBool>
{
    protected override async Task<MyTelegram.Schema.IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestResendPasswordEmail obj)
    {
        var hasPendingCode = await twoFactorService.HasPendingRecoveryEmailCodeAsync(input.UserId);
        if (!hasPendingCode)
        {
            RpcErrors.RpcErrors400.EmailHashExpired.ThrowRpcError();
        }

        var doc = await twoFactorService.GetPasswordAsync(input.UserId);
        if (doc == null || string.IsNullOrEmpty(doc.RecoveryEmail))
        {
            RpcErrors.RpcErrors400.EmailHashExpired.ThrowRpcError();
        }

        var code = RandomNumberGenerator.GetBytes(4);
        var codeString = BitConverter.ToString(code).Replace("-", "").Substring(0, 6);
        await twoFactorService.SetRecoveryEmailAsync(input.UserId, doc.RecoveryEmail, codeString);
        await emailSender.SendVerificationCodeAsync(doc.RecoveryEmail, "Testgram Recovery Email Confirmation", codeString);
        return new MyTelegram.Schema.TBoolTrue();
    }
}
