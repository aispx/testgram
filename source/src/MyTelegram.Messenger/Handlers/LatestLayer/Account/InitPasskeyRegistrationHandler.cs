using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class InitPasskeyRegistrationHandler(
    IPasskeyService passkeyService,
    IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestInitPasskeyRegistration, MyTelegram.Schema.Account.IPasskeyRegistrationOptions>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Account.IPasskeyRegistrationOptions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestInitPasskeyRegistration obj)
    {
        var user = await userAppService.GetAsync(input.UserId);
        var userName = user?.UserName ?? user?.FirstName ?? input.UserId.ToString();
        var json = await passkeyService.CreateRegistrationOptionsAsync(input.UserId, userName);
        return new TPasskeyRegistrationOptions { Options = new TDataJSON { Data = json } };
    }
}
