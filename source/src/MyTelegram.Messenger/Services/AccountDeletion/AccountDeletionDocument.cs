using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.AccountDeletion;

/// <summary>
/// A deletion that was requested for an account protected by a 2FA password whose owner did not
/// provide the password. The account is kept alive for a week so the real owner can cancel the
/// deletion by confirming the phone number, see
/// https://corefork.telegram.org/api/account-deletion.
/// </summary>
public class AccountDeletionDocument
{
    /// <summary>The account to delete; also the document id, so one account has at most one pending deletion.</summary>
    [BsonId]
    public long UserId { get; set; }

    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>The <c>reason</c> passed to <c>account.deleteAccount</c>, empty when the deletion is automatic.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The hash carried by the <c>t.me/confirmphone?phone=…&amp;hash=…</c> link, and the value
    /// <c>account.sendConfirmPhoneCode</c> is called with.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>When the account gets deleted unless the phone number is confirmed before that.</summary>
    public DateTime DeleteAt { get; set; }

    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// The session that asked for the deletion. Confirming the phone number logs exactly this
    /// session out - it is, by construction, somebody who could not produce the 2FA password.
    /// </summary>
    public long RequestedByPermAuthKeyId { get; set; }

    public long RequestedByAuthKeyId { get; set; }

    /// <summary>Phone code hash of the confirmation code sent by <c>account.sendConfirmPhoneCode</c>.</summary>
    public string? PhoneCodeHash { get; set; }

    /// <summary>
    /// Wrong confirmation codes tolerated before the pending code is discarded. The code guards
    /// cancellation of an account deletion, so it must not be brute-forceable within its lifetime.
    /// </summary>
    public int FailedConfirmCount { get; set; }

    /// <summary>Sweeper lease: set while a background pass is executing this deletion.</summary>
    public DateTime? ClaimedUntil { get; set; }
}
