using System.Security.Cryptography;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetPasswordHandler(ITwoFactorService twoFactorService, IRandomHelper randomHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPassword, MyTelegram.Schema.Account.IPassword>
{
    protected override async Task<IPassword> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPassword obj)
    {
        var secureRandom = new byte[256];
        randomHelper.NextBytes(secureRandom);

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
            NewSecureAlgo = new TSecurePasswordKdfAlgoUnknown(),
            SecureRandom = secureRandom
        };

        if (doc != null)
        {
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
