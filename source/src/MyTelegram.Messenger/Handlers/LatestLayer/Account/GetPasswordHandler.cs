using System.Security.Cryptography;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetPasswordHandler(
    ITwoFactorService twoFactorService,
    MyTelegram.Messenger.Services.Passport.IPassportValueStore passportValueStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPassword, MyTelegram.Schema.Account.IPassword>
{
    /// <summary>
    /// Server salt of the passport-secret KDF. The client appends 32 bytes of its own and stores the
    /// concatenation as <c>secureSecretSettings.secure_algo.salt</c>.
    /// See https://corefork.telegram.org/passport/encryption#passport-secret-encryption
    /// </summary>
    private const int SecureSaltLength = 8;

    protected override async Task<IPassword> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPassword obj)
    {
        // Clients use secure_random as salt entropy for the Passport secure secret, so it has to come from a
        // CSPRNG - the same rule the security guidelines state for DH entropy.
        var secureRandom = RandomNumberGenerator.GetBytes(256);

        var doc = await twoFactorService.GetPasswordAsync(input.UserId);

        var newAlgo = new TPasswordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow
        {
            Salt1 = RandomNumberGenerator.GetBytes(32),
            Salt2 = RandomNumberGenerator.GetBytes(32),
            G = SrpConstants.G,
            P = SrpConstants.P2048
        };

        var password = new TPassword
        {
            NewAlgo = newAlgo,
            // "The server should always return a securePasswordKdfAlgoPBKDF2HMACSHA512iter100000
            // constructor in the new_algo field. If securePasswordKdfAlgoUnknown is returned, [...] the
            // user should update their app" - i.e. the clients refuse to set up Passport at all.
            // https://corefork.telegram.org/passport/encryption
            NewSecureAlgo = new TSecurePasswordKdfAlgoPBKDF2HMACSHA512iter100000
            {
                Salt = RandomNumberGenerator.GetBytes(SecureSaltLength)
            },
            SecureRandom = secureRandom,
            HasSecureValues = await passportValueStore.HasAnyAsync(input.UserId)
        };

        if (doc != null)
        {
            password.EmailUnconfirmedPattern = await twoFactorService.HasPendingRecoveryEmailCodeAsync(input.UserId)
                ? twoFactorService.GetRecoveryEmailPattern(doc.RecoveryEmail)
                : null;
            password.LoginEmailPattern = password.EmailUnconfirmedPattern == null
                ? twoFactorService.GetRecoveryEmailPattern(doc.RecoveryEmail)
                : null;
            password.HasRecovery = !string.IsNullOrEmpty(doc.RecoveryEmail) && password.EmailUnconfirmedPattern == null;

            var resetState = await twoFactorService.GetPasswordResetStateAsync(input.UserId);
            if (resetState.HasValue)
            {
                password.PendingResetDate = (int)new DateTimeOffset(resetState.Value.AddDays(7)).ToUnixTimeSeconds();
            }

            var (srpB, srpId) = await twoFactorService.GenerateSrpParamsAsync(input.UserId);
            password.HasPassword = true;
            password.CurrentAlgo = new TPasswordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow
            {
                Salt1 = doc.Salt1,
                Salt2 = doc.Salt2,
                G = doc.G,
                P = doc.P
            };
            password.SrpB = srpB;
            password.SrpId = srpId;
            password.Hint = doc.Hint;
        }

        return password;
    }
}
