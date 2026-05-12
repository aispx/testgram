using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class RegisterPasskeyHandler(IPasskeyService passkeyService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestRegisterPasskey, MyTelegram.Schema.IPasskey>
{
    protected override async Task<MyTelegram.Schema.IPasskey> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestRegisterPasskey obj)
    {
        if (obj.Credential is not TInputPasskeyCredentialPublicKey cred || cred.Response is not TInputPasskeyResponseRegister response)
        {
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        try
        {
            var clientDataJson = PasskeyService.DecodeClientDataJson(response.ClientData.Data);
            var doc = await passkeyService.VerifyRegistrationAsync(input.UserId, cred.Id, cred.RawId, clientDataJson, response.AttestationData.ToArray());
            await passkeyService.SaveAsync(doc);

            return new TPasskey
            {
                Id = doc.Id,
                Name = doc.Name,
                Date = doc.CreatedAt,
                SoftwareEmojiId = doc.SoftwareEmojiId,
                LastUsageDate = doc.LastUsageDate
            };
        }
        catch
        {
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }
    }
}
