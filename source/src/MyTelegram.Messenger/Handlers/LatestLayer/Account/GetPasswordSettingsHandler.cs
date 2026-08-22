using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetPasswordSettingsHandler(ITwoFactorService twoFactorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPasswordSettings, MyTelegram.Schema.Account.IPasswordSettings>
{
    protected override async Task<MyTelegram.Schema.Account.IPasswordSettings> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPasswordSettings obj)
    {
        if (obj.Password is MyTelegram.Schema.TInputCheckPasswordSRP srp)
        {
            var ok = await twoFactorService.VerifySrpAsync(input.UserId, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray());
            if (!ok) RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }

        var recoveryEmail = await twoFactorService.GetRecoveryEmailAsync(input.UserId);
        var secureSettings = await twoFactorService.GetSecureSettingsAsync(input.UserId);

        return new MyTelegram.Schema.Account.TPasswordSettings
        {
            Email = recoveryEmail,
            // The encrypted passport secret. Without it the client cannot decrypt a single stored
            // document. https://corefork.telegram.org/passport/encryption#passport-secret-decryption
            SecureSettings = secureSettings == null
                ? null
                : new TSecureSecretSettings
                {
                    SecureAlgo = new TSecurePasswordKdfAlgoPBKDF2HMACSHA512iter100000
                    {
                        Salt = secureSettings.Salt
                    },
                    SecureSecret = secureSettings.SecureSecret,
                    SecureSecretId = secureSettings.SecureSecretId
                }
        };
    }
}
