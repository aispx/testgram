namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Reset the <a href="https://core.telegram.org/api/auth#email-verification">login email »</a>.
/// Possible errors
/// Code Type Description
/// 400 EMAIL_INSTALL_MISSING  
/// 400 PHONE_NUMBER_INVALID The phone number is invalid.
/// 400 TASK_ALREADY_EXISTS An email reset was already requested.
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.resetLoginEmail"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class ResetLoginEmailHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IVerificationCodeGenerator verificationCodeGenerator,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ICountryHelper countryHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestResetLoginEmail, MyTelegram.Schema.Auth.ISentCode>
{
    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Auth.RequestResetLoginEmail obj)
    {
        // Requirement 5.5: evaluate error conditions in the documented order and throw on the first
        // applicable one, before publishing any command, so a rejected request mutates no state.

        // (1) Requirement 5.2 — invalid phone number. Note: resetLoginEmail returns 400 (not 406,
        // which resend/cancel use), per the auth.resetLoginEmail method page.
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        CheckPhoneNumber(phoneNumber);

        // (2) Requirement 5.3 — the App_Code must have a login email pending verification. When no
        // App_Code exists, or it has no associated login email, the reset cannot proceed.
        var appCode = await queryProcessor.ProcessAsync(new GetLatestAppCodeQuery(phoneNumber, obj.PhoneCodeHash));
        if (appCode == null || string.IsNullOrEmpty(appCode.Email))
        {
            AuthExtraRpcErrors.EmailInstallMissing.ThrowRpcError();
        }

        // (3) Requirement 5.4 — a reset must not already have been requested for this App_Code.
        //
        // Field-availability constraint: the LoginEmailResetRequested flag (the design's mapping of
        // reset_pending_date) lives on AppCodeState/AppCodeSnapshot but is not projected onto
        // IAppCodeReadModel (which exposes only Code/Expire/PhoneCodeHash/PhoneNumber/Email). It is
        // therefore not observable from the read side here, so the TASK_ALREADY_EXISTS guard cannot
        // be evaluated at the handler level with the currently-available read-model fields. This
        // mirrors the field-availability documentation approach used by ResendCodeHandler.
        //
        // In practice this branch is unreachable under the current SMS-only flow: SendCodeHandler
        // never emits an auth.sentCodeTypeEmailCode, so no login email is ever configured and step
        // (2) short-circuits to EMAIL_INSTALL_MISSING before a reset can be requested (see the
        // design "Reality note / SMS-only"). Should the login-email verification flow later be
        // added, the idempotence guard should be enforced either by projecting
        // LoginEmailResetRequested onto the read model, or in AppCodeAggregate.ResetLoginEmail
        // (raising TASK_ALREADY_EXISTS when the flag is already set).

        // (4) Requirement 5.1 — publish the reset (keeping the same phone_code_hash) which switches
        // delivery back to the SMS medium, and return a SentCode describing that new medium.
        var code = verificationCodeGenerator.Generate();
        var now = DateTime.UtcNow.ToTimestamp();
        var appCodeId = AppCodeId.Create(phoneNumber, obj.PhoneCodeHash);
        await commandBus.PublishAsync(new ResetLoginEmailCommand(
            appCodeId,
            input.ToRequestInfo(),
            code,
            now));

        return new TSentCode
        {
            Type = new TSentCodeTypeSms
            {
                Length = code.Length
            },
            PhoneCodeHash = obj.PhoneCodeHash,
            Timeout = options.CurrentValue.VerificationCodeExpirationSeconds
        };
    }

    private void CheckPhoneNumber(string phoneNumber)
    {
        if (!long.TryParse(phoneNumber, out _))
        {
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        }

        if (options.CurrentValue.CheckPhoneNumberFormat)
        {
            if (phoneNumber.Length < 5)
            {
                RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
            }

            var phoneNumberWithoutCountryCode = phoneNumber;
            CountryCodeItem? countryCodeItem = null;
            var maxCountryCodeLength = 4;
            for (var i = 1; i <= maxCountryCodeLength; i++)
            {
                if (countryHelper.TryGetCountryCodeItem(phoneNumber[..i], out countryCodeItem))
                {
                    phoneNumberWithoutCountryCode = phoneNumber[i..];
                    break;
                }
            }

            var phoneNumberLength = phoneNumberWithoutCountryCode.Length;
            if (countryCodeItem?.PhoneNumberLengths?.Count > 0)
            {
                var isValidPhoneNumber = countryCodeItem.PhoneNumberLengths.Any(p => p == phoneNumberLength);
                if (!isValidPhoneNumber)
                {
                    RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
                }
            }
        }
    }
}
