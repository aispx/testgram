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
    /// <summary>
    /// When the password was last set or changed. account.deleteAccount without a password only
    /// delays deletion when the password is older than a week, see
    /// https://corefork.telegram.org/api/account-deletion. A null value means the document predates
    /// this field and is treated as "changed long ago".
    /// </summary>
    public DateTime? PasswordUpdatedAt { get; set; }

    /// <summary>
    /// <c>passport_secret_salt</c> of the <c>securePasswordKdfAlgoPBKDF2HMACSHA512iter100000</c> the
    /// client used to encrypt the passport secret (server salt + client salt, concatenated by the
    /// client). Opaque to the server, echoed back through account.getPasswordSettings.
    /// See https://corefork.telegram.org/passport/encryption
    /// </summary>
    public byte[]? SecureAlgoSalt { get; set; }

    /// <summary>The passport secret, encrypted with the user's 2FA password. Never decryptable here.</summary>
    public byte[]? SecureSecret { get; set; }

    /// <summary>Fingerprint of the passport secret; account.saveSecureValue must quote it.</summary>
    public long SecureSecretId { get; set; }

    public bool IsPasswordResetRequested { get; set; }
    public DateTime? PasswordResetRequestedAt { get; set; }
    public DateTime? PasswordResetRetryAt { get; set; }
}
