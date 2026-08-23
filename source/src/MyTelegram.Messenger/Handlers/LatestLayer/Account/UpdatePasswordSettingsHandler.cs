using System.Security.Cryptography;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdatePasswordSettingsHandler(
    ITwoFactorService twoFactorService,
    IEmailSender emailSender,
    IPassportValueStore passportValueStore,
    IPassportErrorStore passportErrorStore,
    IPassportVerificationStore passportVerificationStore)
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

        // new_algo / new_password_hash / hint all sit behind flag 0. Without this guard a call that only
        // carries new_secure_settings - which is exactly what Telegram Passport setup sends, see
        // https://corefork.telegram.org/passport/encryption#passport-secret-encryption - would fall into
        // the "no new hash, remove the password" branch below and disable 2FA.
        if ((settings.Flags & 1) != 0)
        {
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
            else
            {
                if (currentPassword == null)
                {
                    RpcErrors.RpcErrors400.NewSettingsEmpty.ThrowRpcError();
                }

                await twoFactorService.RemovePasswordAsync(input.UserId);

                // "If the password is disabled, all Telegram Passport data is lost" - the passport secret
                // was only ever recoverable through the password, so leaving the documents behind would
                // keep undecryptable copies of the user's ID on the server forever.
                // https://corefork.telegram.org/passport/encryption
                await passportValueStore.DeleteAllAsync(input.UserId);
                await passportErrorStore.ClearAllAsync(input.UserId);
                await passportVerificationStore.ClearAsync(input.UserId);
            }
        }

        if (settings.NewSecureSettings is TSecureSecretSettings secureSettings)
        {
            if (secureSettings.SecureSecret.Length == 0)
            {
                // An empty secret is how a client drops Telegram Passport without touching the password.
                await twoFactorService.ClearSecureSettingsAsync(input.UserId);
                await passportValueStore.DeleteAllAsync(input.UserId);
                await passportErrorStore.ClearAllAsync(input.UserId);
            }
            else
            {
                if (secureSettings.SecureAlgo is not TSecurePasswordKdfAlgoPBKDF2HMACSHA512iter100000 secureAlgo)
                {
                    // Only the current algorithm is accepted: storing a secret under a legacy KDF would
                    // hand it back to clients that must then re-encrypt it, which is what this call was
                    // supposed to do in the first place.
                    RpcErrors.RpcErrors400.NewSaltInvalid.ThrowRpcError();
                    return null!;
                }

                await twoFactorService.SetSecureSettingsAsync(input.UserId,
                    secureAlgo.Salt.ToArray(),
                    secureSettings.SecureSecret.ToArray(),
                    secureSettings.SecureSecretId);
            }
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
