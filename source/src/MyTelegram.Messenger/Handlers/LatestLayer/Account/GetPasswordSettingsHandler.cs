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

        return new MyTelegram.Schema.Account.TPasswordSettings
        {
            Email = recoveryEmail
        };
    }
}
