namespace MyTelegram.Domain.Aggregates.AppCode;

public class AppCodeState : AggregateState<AppCodeAggregate, AppCodeId, AppCodeState>,
    IApply<AppCodeCreatedEvent>,
    IApply<AppCodeCanceledEvent>,
    IApply<AppCodeResendEvent>,
    IApply<AppCodeLoginEmailResetEvent>,
    IApply<AppCodePaidAuthRequiredEvent>,
    IApply<AppCodePaidAuthCompletedEvent>,
    IApply<SignUpRequiredSagaEvent>,
    //IApply<AppCodeCheckFailedEvent>,
    IApply<CheckSignUpCodeCompletedEvent>,
    IApply<CheckSignInCodeCompletedEvent>//,
    //IApply<CheckAppCodeCompletedEvent>

{
    public bool Canceled { get; private set; }
    public string Code { get; private set; } = default!;
    public string? Email { get; private set; }
    public int Expire { get; private set; }
    public int FailedCount { get; private set; }
    public DateTime LastEmailCodeSendDate { get; private set; }
    public DateTime LastSmsCodeSendDate { get; private set; }
    public string? PhoneNumber { get; private set; }
    public string PhoneCodeHash { get; private set; } = default!;
    public int TodaySentCount { get; private set; }
    public int TotalSentCount { get; private set; }
    public AppCodeType AppCodeType { get; private set; }
    public long UserId { get; private set; }
    public bool LoginEmailResetRequested { get; private set; }
    public bool PaidAuthRequired { get; private set; }
    public long PaidAuthFormId { get; private set; }
    public bool PaidAuthCompleted { get; private set; }

    /// <summary>
    /// Set once <c>auth.signIn</c> has accepted the phone code for a phone number that has no
    /// account yet. <c>auth.signUp</c> carries no phone code of its own, so this flag is the only
    /// evidence that the caller ever proved possession of the code.
    /// </summary>
    public bool SignUpAllowed { get; private set; }
    public void Apply(AppCodeCanceledEvent aggregateEvent)
    {
        Canceled = true;
    }

    public void Apply(AppCodeResendEvent aggregateEvent)
    {
        // Keep the same phone_code_hash / AppCodeId; refresh the pending code value,
        // the recorded delivery medium and the send counters/timestamps.
        Code = aggregateEvent.Code;
        AppCodeType = aggregateEvent.SentCodeType;
        FailedCount = 0;

        TotalSentCount++;
        TodaySentCount++;

        var sendDate = DateTimeOffset.FromUnixTimeSeconds(aggregateEvent.CreationTime).UtcDateTime;
        var isEmailMedium = aggregateEvent.SentCodeType is AppCodeType.SignInEmailCode
            or AppCodeType.PasswordConfirmEmailCode
            or AppCodeType.RecoverPasswordEmailCode
            or AppCodeType.SetupEmailCode
            or AppCodeType.ChangeEmailCode
            or AppCodeType.PassportEmailCode;

        if (isEmailMedium)
        {
            LastEmailCodeSendDate = sendDate;
        }
        else
        {
            LastSmsCodeSendDate = sendDate;
        }
    }

    public void Apply(AppCodeLoginEmailResetEvent aggregateEvent)
    {
        // Mark that a login-email reset has been requested (semantically equivalent to
        // reset_pending_date being set) and switch delivery back to the SMS medium, refreshing
        // the pending code value.
        LoginEmailResetRequested = true;
        Code = aggregateEvent.Code;
        AppCodeType = AppCodeType.SignInSmsCode;

        var sendDate = DateTimeOffset.FromUnixTimeSeconds(aggregateEvent.CreationTime).UtcDateTime;
        LastSmsCodeSendDate = sendDate;
    }

    public void Apply(AppCodePaidAuthRequiredEvent aggregateEvent)
    {
        // A code delivery requires payment: record the pending payment form and mark the
        // authorization as payment-required, resetting any previous completion flag.
        PaidAuthRequired = true;
        PaidAuthFormId = aggregateEvent.FormId;
        PaidAuthCompleted = false;
    }

    public void Apply(AppCodePaidAuthCompletedEvent aggregateEvent)
    {
        // Only clear the payment requirement when the completing form id matches the pending
        // PaidAuthFormId (and a payment was actually required). A mismatched form id leaves the
        // paid-auth state unchanged (still payment-required).
        if (PaidAuthRequired && PaidAuthFormId == aggregateEvent.FormId)
        {
            PaidAuthCompleted = true;
            PaidAuthRequired = false;
        }
    }

    //public void Apply(AppCodeCheckFailedEvent aggregateEvent)
    //{
    //    FailedCount++;
    //}

    public void Apply(AppCodeCreatedEvent aggregateEvent)
    {
        PhoneNumber = aggregateEvent.PhoneNumber;
        PhoneCodeHash = aggregateEvent.PhoneCodeHash;
        Code = aggregateEvent.Code;
        Expire = aggregateEvent.Expire;
        FailedCount = 0;
    }

    public void Apply(CheckSignInCodeCompletedEvent aggregateEvent)
    {
        if (!aggregateEvent.IsCodeValid)
        {
            FailedCount++;
            return;
        }

        // Accepting the code here is the caller's proof that they possess it, which is what
        // auth.signUp relies on since it carries no code of its own.
        SignUpAllowed = true;
    }

    public void Apply(CheckSignUpCodeCompletedEvent aggregateEvent)
    {
        if (!aggregateEvent.IsCodeValid)
        {
            FailedCount++;
        }
    }

    public void Apply(SignUpRequiredSagaEvent aggregateEvent)
    {
    }

    //public void Apply(CheckAppCodeCompletedEvent aggregateEvent)
    //{
    //    if (!aggregateEvent.IsValidCode)
    //    {
    //        FailedCount++;
    //    }
    //}

    public void LoadSnapshot(AppCodeSnapshot snapshot)
    {
        UserId = snapshot.UserId;
        Expire = snapshot.Expire;
        FailedCount = snapshot.FailedCount;
        PhoneNumber = snapshot.PhoneNumber;
        PhoneCodeHash = snapshot.PhoneCodeHash;
        Code = snapshot.Code;
        Email = snapshot.Email;
        LastSmsCodeSendDate = snapshot.LastSmsCodeSendDate;
        LastEmailCodeSendDate = snapshot.LastEmailCodeSendDate;
        TotalSentCount = snapshot.TotalSentCount;
        TodaySentCount = snapshot.TodaySentCount;
        AppCodeType = snapshot.AppCodeType;
        LoginEmailResetRequested = snapshot.LoginEmailResetRequested;
        PaidAuthRequired = snapshot.PaidAuthRequired;
        PaidAuthFormId = snapshot.PaidAuthFormId;
        PaidAuthCompleted = snapshot.PaidAuthCompleted;
        SignUpAllowed = snapshot.SignUpAllowed;
    }
}
