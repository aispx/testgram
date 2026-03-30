using MyTelegram.Messenger.Services.Passkey;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class RegisterPasskeyHandler(IPasskeyService passkeyService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestRegisterPasskey, MyTelegram.Schema.IPasskey>
{
    protected override async Task<MyTelegram.Schema.IPasskey> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestRegisterPasskey obj)
    {
        if (obj.Credential is not TInputPasskeyCredentialPublicKey cred)
        {
            RpcErrors.RpcErrors400.CredentialInvalid.ThrowRpcError();
            return null!;
        }

        var response = (TInputPasskeyResponseRegister)cred.Response;
        var clientDataJson = PasskeyService.DecodeClientDataJson(response.ClientData.Data);

        var doc = await passkeyService.VerifyRegistrationAsync(input.UserId, cred.Id, clientDataJson, response.AttestationData.ToArray());
        await passkeyService.SaveAsync(doc);

        return new TPasskey { Id = doc.Id, Name = doc.Name, Date = doc.CreatedAt };
    }
}
