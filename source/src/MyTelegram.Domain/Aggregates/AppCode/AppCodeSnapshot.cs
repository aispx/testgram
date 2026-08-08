namespace MyTelegram.Domain.Aggregates.AppCode;

public record AppCodeSnapshot(
    long UserId,
    int Expire,
    int FailedCount,
    string? PhoneNumber,
    string PhoneCodeHash,
    string Code,
    string? Email,
    DateTime LastSmsCodeSendDate,
    DateTime LastEmailCodeSendDate,
    int TotalSentCount,
    int TodaySentCount,
    AppCodeType AppCodeType,
    bool LoginEmailResetRequested = false,
    bool PaidAuthRequired = false,
    long PaidAuthFormId = 0,
    bool PaidAuthCompleted = false,
    bool SignUpAllowed = false
    )
    : ISnapshot;