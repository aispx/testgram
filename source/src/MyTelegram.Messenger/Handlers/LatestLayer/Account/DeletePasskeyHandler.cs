using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class DeletePasskeyHandler(IPasskeyService passkeyService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestDeletePasskey, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestDeletePasskey obj)
    {
        await passkeyService.DeletePasskeyAsync(input.UserId, obj.Id);
        return new TBoolTrue();
    }
}
