using System.Security.Cryptography;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdatePasswordSettingsHandler(ITwoFactorService twoFactorService, IEmailSender emailSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdatePasswordSettings, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdatePasswordSettings obj)
    {
        var currentPassword = await twoFactorService.GetPasswordAsync(input.UserId);

        // Verify current password if set
        if (obj.Password is TInputCheckPasswordSRP srp)
        {
            var ok = await twoFactorService.VerifySrpAsync(input.UserId, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray());
            if (!ok) RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }
        else if (currentPassword != null)
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }

        var settings = obj.NewSettings as TPasswordInputSettings;
        if (settings == null) return new TBoolTrue();

        if (settings.NewAlgo is TPasswordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow algo
            && settings.NewPasswordHash is { Length: > 0 })
        {
            // Set new password
            await twoFactorService.SetPasswordAsync(
                input.UserId,
                algo.Salt1,
                algo.Salt2,
                settings.NewPasswordHash,
                settings.Hint);
        }
        else if (settings.NewAlgo is TPasswordKdfAlgoUnknown || settings.NewPasswordHash is null or { Length: 0 })
        {
            if (currentPassword == null)
            {
                RpcErrors.RpcErrors400.NewSettingsEmpty.ThrowRpcError();
            }

            await twoFactorService.RemovePasswordAsync(input.UserId);
        }

        if (settings.Email != null && (settings.Flags & 2) != 0)
        {
            var code = RandomNumberGenerator.GetBytes(4);
            var codeString = BitConverter.ToString(code).Replace("-", "").Substring(0, 6);
            await twoFactorService.SetRecoveryEmailAsync(input.UserId, settings.Email, codeString);
            await emailSender.SendVerificationCodeAsync(settings.Email, "Testgram Recovery Email Confirmation", codeString);
            RpcErrors.RpcErrors400.EmailUnconfirmedX.ThrowRpcError(codeString.Length);
        }

        return new TBoolTrue();
    }
}
