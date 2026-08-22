using MongoDB.Driver;
using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Delete the user's account from the telegram servers.Can also be used to delete the account of a user that provided the login code, but forgot the 2FA password and no recovery method is configured, see <a href="https://corefork.telegram.org/api/srp#password-recovery">here »</a> for more info on password recovery, and <a href="https://corefork.telegram.org/api/account-deletion">here »</a> for more info on account deletion.
/// Possible errors
/// Code Type Description
/// 420 2FA_CONFIRM_WAIT_%d Since this account is active and protected by a 2FA password, we will delete it in 1 week for security purposes. You can cancel this process at any time, you'll be able to reset your account in %d seconds.
/// 400 PASSWORD_HASH_INVALID The provided password hash is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.deleteAccount"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class DeleteAccountHandler(
    ITwoFactorService twoFactorService,
    IAccountDeletionService accountDeletionService,
    IUserAppService userAppService,
    IObjectMessageSender objectMessageSender,
    IMongoDatabase database,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<DeleteAccountHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestDeleteAccount, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestDeleteAccount obj)
    {
        // The method is callable before the 2FA password was checked, but the session is already
        // bound to the account by then. Without that binding there is no account to delete.
        var userId = input.UserId;
        if (userId == 0)
        {
            RpcErrors.RpcErrors401.AuthKeyUnregistered.ThrowRpcError();
        }

        var user = await userAppService.GetAsync((long?)userId);
        if (user == null || user.IsDeleted == true)
        {
            RpcErrors.RpcErrors401.AuthKeyUnregistered.ThrowRpcError();
        }

        // Service accounts (notification, support, replies, anonymous) and bots stay: they are shared
        // infrastructure, and a bot is removed through BotFather instead.
        if (accountDeletionService.IsProtectedFromDeletion(user!))
        {
            RpcErrors.RpcErrors403.UserRestricted.ThrowRpcError();
        }

        var reason = obj.Reason ?? string.Empty;
        var passwordDocument = await twoFactorService.GetPasswordAsync(userId);

        // No 2FA password: nothing to prove, the account goes away right now.
        if (passwordDocument == null)
        {
            await accountDeletionService.DeleteAccountAsync(userId, reason, input.ToRequestInfo());
            return new TBoolTrue();
        }

        if (obj.Password != null)
        {
            await CheckPasswordAsync(userId, obj.Password);
            await accountDeletionService.DeleteAccountAsync(userId, reason, input.ToRequestInfo());
            return new TBoolTrue();
        }

        var config = options.CurrentValue.AccountDeletion;
        var now = DateTime.UtcNow;

        // A deletion is already parked: report the remaining wait instead of re-arming the timer,
        // which would let a caller push the deadline back (and re-notify) at will.
        var pending = await accountDeletionService.GetPendingByUserIdAsync(userId);
        if (pending != null)
        {
            ThrowConfirmWait(pending.DeleteAt, now);
        }

        var delay = TimeSpan.FromDays(config.TwoFaDelayDays);
        var passwordChangedRecently = passwordDocument.PasswordUpdatedAt.HasValue &&
                                      passwordDocument.PasswordUpdatedAt.Value > now - delay;
        var lastOnline = await GetLastOnlineAsync(userId);
        var activeRecently = lastOnline.HasValue && lastOnline.Value > now - delay;

        // "If the account's 2FA password was modified more than 7 days ago and was active in the
        // last 7 days, account deletion will be delayed for 7 days. Otherwise, the account will be
        // immediately deleted." — https://corefork.telegram.org/api/account-deletion
        if (passwordChangedRecently || !activeRecently)
        {
            await accountDeletionService.DeleteAccountAsync(userId, reason, input.ToRequestInfo());
            return new TBoolTrue();
        }

        var deleteAt = now + delay;
        var document = await accountDeletionService.SchedulePendingAsync(userId,
            user!.PhoneNumber,
            reason,
            deleteAt,
            input.ToRequestInfo());

        await SendConfirmPhoneNotificationAsync(input, user, document);

        logger.LogInformation("Account {UserId} deletion delayed until {DeleteAt}", userId, deleteAt);

        ThrowConfirmWait(deleteAt, now);

        return new TBoolTrue();
    }

    private async Task CheckPasswordAsync(long userId, IInputCheckPasswordSRP password)
    {
        if (password is not TInputCheckPasswordSRP srp)
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
            return;
        }

        var srpUserId = await twoFactorService.GetUserIdBySrpIdAsync(srp.SrpId);
        if (srpUserId != userId)
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }

        var passwordValid = await twoFactorService.VerifySrpAsync(userId, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray());
        if (!passwordValid)
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }
    }

    private static void ThrowConfirmWait(DateTime deleteAt, DateTime now)
    {
        var secondsLeft = (int)Math.Max(1, (deleteAt - now).TotalSeconds);
        RpcErrors.RpcErrors420._2faConfirmWaitX.ThrowRpcError(secondsLeft);
    }

    /// <summary>
    /// Last reported presence, persisted by the user status cache. A user that never came online
    /// has no entry, which counts as inactive and therefore as "delete immediately".
    /// </summary>
    private async Task<DateTime?> GetLastOnlineAsync(long userId)
    {
        var status = await database.GetCollection<UserStatusMongoModel>("user_status")
            .Find(p => p.UserId == userId)
            .FirstOrDefaultAsync();

        return status?.LastOnline;
    }

    /// <summary>
    /// Tells the account owner that somebody is deleting their account, and how to stop it: the
    /// <a href="https://corefork.telegram.org/api/links#phone-confirmation-links">phone confirmation
    /// link</a> the client turns into an account.sendConfirmPhoneCode call.
    /// </summary>
    private async Task SendConfirmPhoneNotificationAsync(IRequestInput input,
        IUserReadModel user,
        AccountDeletionDocument document)
    {
        var domain = options.CurrentValue.JoinChatDomain;
        var link = $"https://{domain}/confirmphone?phone={user.PhoneNumber}&hash={document.Hash}";
        var message = $"""
                       Your account is scheduled for deletion.

                       Somebody requested deletion of this account without entering the two-step verification password. The account and all its data will be deleted on {document.DeleteAt:yyyy-MM-dd HH:mm} UTC.

                       If this wasn't you, open the link below and confirm your phone number to cancel the deletion and log the other session out:
                       {link}
                       """;

        var entities = new List<IMessageEntity>();
        void AddEntity(IMessageEntity? entity)
        {
            if (entity != null)
            {
                entities.Add(entity);
            }
        }

        AddEntity(message.CreateMessageEntityBold("Your account is scheduled for deletion."));
        AddEntity(message.CreateMessageEntityUrl(link));

        var updates = new TUpdates
        {
            Updates =
            [
                new TUpdateServiceNotification
                {
                    InboxDate = CurrentDate,
                    Type = "AccountDeletion",
                    Message = message,
                    Media = new TMessageMediaEmpty(),
                    Entities = [.. entities]
                }
            ],
            Chats = [],
            Users = [],
            Date = CurrentDate
        };

        // Everybody but the session that asked for the deletion: that one is, by construction,
        // somebody who could not produce the password.
        await objectMessageSender.PushMessageToPeerAsync(user.UserId.ToUserPeer(), updates,
            excludeAuthKeyId: input.AuthKeyId);
    }
}
