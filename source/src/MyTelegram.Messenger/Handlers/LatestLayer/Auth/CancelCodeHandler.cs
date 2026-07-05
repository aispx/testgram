namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Cancel the login verification code
/// Possible errors
/// Code Type Description
/// 400 PHONE_CODE_EXPIRED The phone code you provided has expired.
/// 406 PHONE_NUMBER_INVALID The phone number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.cancelCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class CancelCodeHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestCancelCode, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestCancelCode obj)
    {
        // Validation order per design "2. CancelCodeHandler": throw on the first applicable error
        // before publishing any command so a rejected request mutates no state.

        // (1) Requirement 2.3 — invalid phone number.
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        if (!long.TryParse(phoneNumber, out _))
        {
            RpcErrors.RpcErrors406.PhoneNumberInvalid.ThrowRpcError();
        }

        // (2) Requirement 2.4 / 2.5 — the App_Code must exist and not be expired. A missing or
        // already-expired code is a no-op that reports PHONE_CODE_EXPIRED (no state change).
        var appCode = await queryProcessor.ProcessAsync(new GetLatestAppCodeQuery(phoneNumber, obj.PhoneCodeHash));
        var now = DateTime.UtcNow.ToTimestamp();
        if (appCode == null || appCode.Expire < now)
        {
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        }

        // (3) Requirement 2.1 — mark the App_Code as cancelled via the existing
        // AppCodeAggregate.CancelCode → AppCodeCanceledEvent. Requirement 2.2 (a cancelled code
        // makes a later sign-in fail with PHONE_CODE_EXPIRED) is already satisfied by
        // AppCodeAggregate.CheckCodeCore, which treats a cancelled code as expired.
        var appCodeId = AppCodeId.Create(phoneNumber, obj.PhoneCodeHash);
        await commandBus.PublishAsync(new CancelCodeCommand(
            appCodeId,
            input.ToRequestInfo(),
            phoneNumber,
            obj.PhoneCodeHash));

        return new TBoolTrue();
    }
}
