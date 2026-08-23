namespace MyTelegram.Messenger.Services.AccountDeletion;

/// <summary>
/// Account deletion, see https://corefork.telegram.org/api/account-deletion.
/// </summary>
public interface IAccountDeletionService
{
    /// <summary>
    /// Wipes the profile, releases every username, revokes every session and drops the 2FA
    /// password. Messages the user sent to other chats are left alone - the official server keeps
    /// them too, the peer simply renders as <c>Deleted Account</c>.
    /// </summary>
    Task DeleteAccountAsync(long userId, string reason, RequestInfo? requestInfo = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True for accounts that must never be deleted: the built-in system users (notification,
    /// support, anonymous, replies), anything flagged as support, and bots - a bot is removed
    /// through BotFather, not through account.deleteAccount.
    /// </summary>
    bool IsProtectedFromDeletion(IUserReadModel user);

    /// <summary>Parks a deletion for <paramref name="deleteAt"/> and returns the stored record.</summary>
    Task<AccountDeletionDocument> SchedulePendingAsync(long userId,
        string phoneNumber,
        string reason,
        DateTime deleteAt,
        RequestInfo requestInfo,
        CancellationToken cancellationToken = default);

    Task<AccountDeletionDocument?> GetPendingByUserIdAsync(long userId, CancellationToken cancellationToken = default);

    Task<AccountDeletionDocument?> GetPendingByHashAsync(string hash, CancellationToken cancellationToken = default);

    Task CancelPendingAsync(long userId, CancellationToken cancellationToken = default);

    Task SetPhoneCodeHashAsync(long userId, string phoneCodeHash, CancellationToken cancellationToken = default);

    /// <summary>Counts a wrong confirmation code and returns the new failure count.</summary>
    Task<int> IncrementFailedConfirmCountAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a lease on one pending deletion that is due, so two sweeper passes (or two command
    /// servers) never execute the same deletion at once. Returns null when nothing is due.
    /// </summary>
    Task<AccountDeletionDocument?> ClaimNextDuePendingAsync(DateTime now,
        TimeSpan claimFor,
        CancellationToken cancellationToken = default);

    /// <summary>Terminates a single session, used to log out whoever requested a cancelled deletion.</summary>
    Task RevokeSessionAsync(long userId, long permAuthKeyId, CancellationToken cancellationToken = default);
}
