using MyTelegram.Messenger.Services.AccountDeletion;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Send confirmation code to cancel account deletion, for more info <a href="https://corefork.telegram.org/api/account-deletion">click here »</a>
/// Possible errors
/// Code Type Description
/// 400 HASH_INVALID The provided hash is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.sendConfirmPhoneCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendConfirmPhoneCodeHandler(
    IAccountDeletionService accountDeletionService,
    ICommandBus commandBus,
    IVerificationCodeGenerator verificationCodeGenerator,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendConfirmPhoneCode, MyTelegram.Schema.Auth.ISentCode>
{
    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSendConfirmPhoneCode obj)
    {
        var pending = await accountDeletionService.GetPendingByHashAsync(obj.Hash);

        // The hash comes from a confirmphone link, and the docs require the method to be called
        // "using the account with the specified phone number" - so it only works for its owner.
        if (pending == null || pending.UserId != input.UserId)
        {
            RpcErrors.RpcErrors400.HashInvalid.ThrowRpcError();
        }

        var phoneNumber = pending!.PhoneNumber.ToPhoneNumber();
        var code = verificationCodeGenerator.Generate();
        var phoneCodeHash = Guid.NewGuid().ToString("N");

        await commandBus.PublishAsync(new CreateAppCodeCommand(
            AppCodeId.Create(phoneNumber, phoneCodeHash),
            input.ToRequestInfo(),
            pending.UserId,
            phoneNumber,
            code,
            phoneCodeHash,
            DateTime.UtcNow.ToTimestamp()));

        await accountDeletionService.SetPhoneCodeHashAsync(pending.UserId, phoneCodeHash);

        return new TSentCode
        {
            Type = new TSentCodeTypeSms
            {
                Length = code.Length
            },
            PhoneCodeHash = phoneCodeHash,
            Timeout = options.CurrentValue.VerificationCodeExpirationSeconds
        };
    }
}
