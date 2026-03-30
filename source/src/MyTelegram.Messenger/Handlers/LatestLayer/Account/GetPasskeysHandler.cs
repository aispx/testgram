using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetPasskeysHandler(IPasskeyService passkeyService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPasskeys, MyTelegram.Schema.Account.IPasskeys>
{
    protected override async Task<MyTelegram.Schema.Account.IPasskeys> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPasskeys obj)
    {
        var docs = await passkeyService.GetPasskeysAsync(input.UserId);
        return new TPasskeys
        {
            Passkeys = new TVector<IPasskey>(docs.Select(d => (IPasskey)new TPasskey
            {
                Id = d.Id,
                Name = d.Name,
                Date = d.CreatedAt,
                SoftwareEmojiId = d.SoftwareEmojiId,
                LastUsageDate = d.LastUsageDate
            }).ToList())
        };
    }
}
