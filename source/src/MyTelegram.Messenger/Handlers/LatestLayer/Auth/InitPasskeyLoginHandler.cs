using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;

internal sealed class InitPasskeyLoginHandler(IPasskeyService passkeyService) : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestInitPasskeyLogin, MyTelegram.Schema.Auth.IPasskeyLoginOptions>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Auth.IPasskeyLoginOptions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Auth.RequestInitPasskeyLogin obj)
    {
        var json = await passkeyService.CreateLoginOptionsAsync();
        return new TPasskeyLoginOptions { Options = new TDataJSON { Data = json } };
    }
}
