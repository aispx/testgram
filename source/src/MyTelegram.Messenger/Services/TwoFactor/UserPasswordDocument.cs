namespace MyTelegram.Messenger.Services.TwoFactor;

public class UserPasswordDocument
{
    public long Id { get; set; } // userId
    public byte[] Salt1 { get; set; } = null!;
    public byte[] Salt2 { get; set; } = null!;
    public byte[] PasswordHash { get; set; } = null!; // v = g^x mod p, stored as big-endian bytes
    public string? Hint { get; set; }
    public int G { get; set; } = SrpConstants.G;
    public byte[] P { get; set; } = SrpConstants.P2048;
    public string? RecoveryEmail { get; set; }
    public string? RecoveryEmailCode { get; set; }
    public DateTime? RecoveryEmailCodeExpire { get; set; }

    /// <summary>
    /// Failed attempts against the pending recovery code. Without a limit the code — which gates a
    /// 2FA password reset — can be brute-forced within its validity window.
    /// </summary>
    public int RecoveryEmailCodeFailedCount { get; set; }
    public bool IsPasswordResetRequested { get; set; }
    public DateTime? PasswordResetRequestedAt { get; set; }
    public DateTime? PasswordResetRetryAt { get; set; }
}
