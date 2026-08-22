using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Confirm a phone number to cancel account deletion, for more info <a href="https://corefork.telegram.org/api/account-deletion">click here »</a>
/// Possible errors
/// Code Type Description
/// 400 CODE_HASH_INVALID Code hash invalid.
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.confirmPhone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ConfirmPhoneHandler(
    IAccountDeletionService accountDeletionService,
    ITwoFactorService twoFactorService,
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    ILogger<ConfirmPhoneHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestConfirmPhone, IBool>
{
    /// <summary>
    /// Wrong codes tolerated before the pending code is thrown away. The code cancels an account
    /// deletion, so it must not survive long enough to be guessed.
    /// </summary>
    private const int MaxFailedConfirmCount = 5;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestConfirmPhone obj)
    {
        if (string.IsNullOrEmpty(obj.PhoneCode))
        {
            RpcErrors.RpcErrors400.PhoneCodeEmpty.ThrowRpcError();
        }

        var pending = await accountDeletionService.GetPendingByUserIdAsync(input.UserId);
        if (pending == null ||
            string.IsNullOrEmpty(pending.PhoneCodeHash) ||
            pending.PhoneCodeHash != obj.PhoneCodeHash)
        {
            RpcErrors.RpcErrors400.CodeHashInvalid.ThrowRpcError();
        }

        var phoneNumber = pending!.PhoneNumber.ToPhoneNumber();
        var appCode = await queryProcessor.ProcessAsync(new GetLatestAppCodeQuery(phoneNumber, obj.PhoneCodeHash));
        if (appCode == null || appCode.Expire < DateTime.UtcNow.ToTimestamp())
        {
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        }

        if (appCode!.Code != obj.PhoneCode)
        {
            var failedCount = await accountDeletionService.IncrementFailedConfirmCountAsync(pending.UserId);
            if (failedCount >= MaxFailedConfirmCount)
            {
                // Burning the code forces the owner through account.sendConfirmPhoneCode again
                // instead of letting the guesses continue against the same code.
                await CancelCodeAsync(input, phoneNumber, obj.PhoneCodeHash);
                await accountDeletionService.SetPhoneCodeHashAsync(pending.UserId, string.Empty);
            }

            RpcErrors.RpcErrors400.PhoneCodeInvalid.ThrowRpcError();
        }

        await CancelCodeAsync(input, phoneNumber, obj.PhoneCodeHash);
        await accountDeletionService.CancelPendingAsync(pending.UserId);

        // The deletion was requested by somebody who could not produce the 2FA password, and a
        // password reset may be running on the same account: both are cancelled here, and the
        // session that started it is logged out, as required by the account deletion docs.
        // Only when a reset is actually pending: declining sets a fresh 7 day retry window, which
        // would otherwise lock the owner out of a reset they never asked to cancel.
        var passwordResetState = await twoFactorService.GetPasswordResetStateAsync(pending.UserId);
        if (passwordResetState.HasValue)
        {
            await twoFactorService.DeclinePasswordResetAsync(pending.UserId);
        }

        await accountDeletionService.RevokeSessionAsync(pending.UserId, pending.RequestedByPermAuthKeyId);

        logger.LogInformation("Account {UserId} deletion cancelled by phone confirmation", pending.UserId);

        return new TBoolTrue();
    }

    private async Task CancelCodeAsync(IRequestInput input, string phoneNumber, string phoneCodeHash)
    {
        await commandBus.PublishAsync(new CancelCodeCommand(
            AppCodeId.Create(phoneNumber, phoneCodeHash),
            input.ToRequestInfo(),
            phoneNumber,
            phoneCodeHash));
    }
}
