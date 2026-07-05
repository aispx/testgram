namespace MyTelegram.Messenger.Handlers.LatestLayer.Auth;
/// <summary>
/// Resend the login code via another medium, the phone code type is determined by the return value of the previous auth.sendCode/auth.resendCode: see <a href="https://corefork.telegram.org/api/auth">login</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 EMAIL_INSTALL_MISSING  
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// 400 PHONE_CODE_EXPIRED The phone code you provided has expired.
/// 400 PHONE_CODE_HASH_EMPTY phone_code_hash is missing.
/// 406 PHONE_NUMBER_INVALID The phone number is invalid.
/// 406 SEND_CODE_UNAVAILABLE Returned when all available options for this type of number were already used (e.g. flash-call, then SMS, then this error might be returned to trigger a second resend).
/// <para><c>See <a href="https://corefork.telegram.org/method/auth.resendCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class ResendCodeHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IVerificationCodeGenerator verificationCodeGenerator,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ICountryHelper countryHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Auth.RequestResendCode, MyTelegram.Schema.Auth.ISentCode>
{
    /// <summary>
    /// Delivery medium for a resend. The current server issues SMS login codes only
    /// (see <see cref="SendCodeHandler"/>); the login-email medium is modelled here for when the
    /// login-email verification flow is added (see the design "Reality note / SMS-only").
    /// </summary>
    private enum ResendMedium
    {
        None,
        Sms,
        LoginEmail
    }

    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Auth.RequestResendCode obj)
    {
        // Requirement 1.8: evaluate error conditions in the documented order and throw on the first
        // applicable one, before publishing any command, so a rejected request mutates no state.

        // (1) Requirement 1.3 — empty phone code hash.
        if (string.IsNullOrEmpty(obj.PhoneCodeHash))
        {
            RpcErrors.RpcErrors400.PhoneCodeHashEmpty.ThrowRpcError();
        }

        // (2) Requirement 1.4 — invalid phone number.
        var phoneNumber = obj.PhoneNumber.ToPhoneNumber();
        CheckPhoneNumber(phoneNumber);

        // (3) Requirement 1.5 — missing / expired / cancelled App_Code.
        var appCode = await queryProcessor.ProcessAsync(new GetLatestAppCodeQuery(phoneNumber, obj.PhoneCodeHash));
        var now = DateTime.UtcNow.ToTimestamp();
        if (appCode == null || appCode.Expire < now)
        {
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        }

        // (4) Requirement 1.6 — a login-email delivery can only be resent when a login email is
        // installed for the App_Code.
        var recordedMedium = ResolveRecordedMedium(appCode!);
        if (recordedMedium == ResendMedium.LoginEmail && string.IsNullOrEmpty(appCode!.Email))
        {
            AuthExtraRpcErrors.EmailInstallMissing.ThrowRpcError();
        }

        // (5) Requirement 1.7 — the medium the resend will use. When every option for the number
        // has already been exhausted no alternate remains.
        var (medium, nextMedium) = ResolveResendMedium(recordedMedium);
        if (medium == ResendMedium.None)
        {
            RpcErrors.RpcErrors406.SendCodeUnavailable.ThrowRpcError();
        }

        // (6) Requirement 1.1 / 1.2 — publish the resend (keeping the same phone_code_hash) and
        // return a SentCode describing the delivery medium. NextType is set only when a further
        // medium remains to advance to.
        var code = verificationCodeGenerator.Generate();
        var appCodeId = AppCodeId.Create(phoneNumber, obj.PhoneCodeHash);
        await commandBus.PublishAsync(new ResendCodeCommand(
            appCodeId,
            input.ToRequestInfo(),
            code,
            ToAppCodeType(medium),
            now));

        return new TSentCode
        {
            Type = ToSentCodeType(medium, code, appCode!.Email),
            PhoneCodeHash = obj.PhoneCodeHash,
            Timeout = options.CurrentValue.VerificationCodeExpirationSeconds,
            NextType = ToCodeType(nextMedium)
        };
    }

    private void CheckPhoneNumber(string phoneNumber)
    {
        if (!long.TryParse(phoneNumber, out _))
        {
            RpcErrors.RpcErrors406.PhoneNumberInvalid.ThrowRpcError();
        }

        if (options.CurrentValue.CheckPhoneNumberFormat)
        {
            if (phoneNumber.Length < 5)
            {
                RpcErrors.RpcErrors406.PhoneNumberInvalid.ThrowRpcError();
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
                    RpcErrors.RpcErrors406.PhoneNumberInvalid.ThrowRpcError();
                }
            }
        }
    }

    /// <summary>
    /// Determine the medium of the App_Code's prior delivery (Requirement 1.2) from its recorded
    /// state. A code with an associated login email was a login-email delivery; otherwise it is an
    /// SMS delivery (the only medium the current server issues).
    /// </summary>
    private static ResendMedium ResolveRecordedMedium(IAppCodeReadModel appCode)
    {
        return string.IsNullOrEmpty(appCode.Email) ? ResendMedium.Sms : ResendMedium.LoginEmail;
    }

    /// <summary>
    /// Resolve the medium the resend will use together with the following medium (next_type), from
    /// the prior delivery medium. The current server is SMS-only, so the SMS medium resends via SMS
    /// with no further medium, and a login-email delivery resends via login email.
    /// </summary>
    private static (ResendMedium medium, ResendMedium next) ResolveResendMedium(ResendMedium recordedMedium)
    {
        return recordedMedium switch
        {
            ResendMedium.LoginEmail => (ResendMedium.LoginEmail, ResendMedium.None),
            ResendMedium.Sms => (ResendMedium.Sms, ResendMedium.None),
            _ => (ResendMedium.None, ResendMedium.None)
        };
    }

    private static AppCodeType ToAppCodeType(ResendMedium medium)
    {
        return medium == ResendMedium.LoginEmail ? AppCodeType.SignInEmailCode : AppCodeType.SignInSmsCode;
    }

    private static ISentCodeType ToSentCodeType(ResendMedium medium, string code, string? email)
    {
        if (medium == ResendMedium.LoginEmail)
        {
            return new TSentCodeTypeEmailCode
            {
                EmailPattern = email ?? string.Empty,
                Length = code.Length
            };
        }

        return new TSentCodeTypeSms
        {
            Length = code.Length
        };
    }

    private static ICodeType? ToCodeType(ResendMedium medium)
    {
        // No further medium remains after the resend in the current SMS-only model.
        return null;
    }
}
