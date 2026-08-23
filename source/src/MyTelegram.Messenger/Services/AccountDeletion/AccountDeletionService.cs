using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Device;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Services.AccountDeletion;

/// <summary>
/// Implements https://corefork.telegram.org/api/account-deletion: immediate deletion, the one week
/// delay for accounts protected by a 2FA password the caller did not provide, and the bookkeeping
/// the confirmphone flow needs to cancel such a delayed deletion.
/// </summary>
public class AccountDeletionService(
    IMongoDatabase database,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IEventBus eventBus,
    ITwoFactorService twoFactorService,
    IPassportValueStore passportValueStore,
    IPassportErrorStore passportErrorStore,
    IPassportVerificationStore passportVerificationStore,
    IUserAppService userAppService,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<AccountDeletionService> logger)
    : IAccountDeletionService, ISingletonDependency
{
    private const string CollectionName = "account_deletions";

    private IMongoCollection<AccountDeletionDocument> Collection =>
        database.GetCollection<AccountDeletionDocument>(CollectionName);

    public async Task DeleteAccountAsync(long userId,
        string reason,
        RequestInfo? requestInfo = null,
        CancellationToken cancellationToken = default)
    {
        var user = await userAppService.GetAsync((long?)userId);
        if (user == null || user.IsDeleted == true)
        {
            await CancelPendingAsync(userId, cancellationToken);
            return;
        }

        // Checked here rather than only at the rpc entry point, so neither the delayed-deletion
        // sweeper nor the self-destruction pass can take a service account down either.
        if (IsProtectedFromDeletion(user))
        {
            logger.LogWarning("Refused to delete protected account {UserId}", userId);
            await CancelPendingAsync(userId, cancellationToken);
            return;
        }

        await ReleaseUserNamesAsync(user, cancellationToken);

        await commandBus.PublishAsync(new DeleteAccountCommand(
            UserId.Create(userId),
            requestInfo ?? CreateRequestInfo(userId),
            reason ?? string.Empty,
            DateTime.UtcNow.ToTimestamp()), cancellationToken);

        await RevokeAllSessionsAsync(userId, cancellationToken);

        await twoFactorService.RemovePasswordAsync(userId);
        await twoFactorService.ClearPasswordResetStateAsync(userId);

        // The passport secret went with the password, so the documents are unreadable from here on -
        // and they are identity papers, which is the last thing to leave behind on a deleted account.
        await passportValueStore.DeleteAllAsync(userId);
        await passportErrorStore.ClearAllAsync(userId);
        await passportVerificationStore.ClearAsync(userId);

        await CancelPendingAsync(userId, cancellationToken);
        userAppService.InvalidateCache(userId);

        logger.LogInformation("Deleted account {UserId}, reason: {Reason}", userId, reason);
    }

    public bool IsProtectedFromDeletion(IUserReadModel user)
    {
        if (PeerKindHelper.IsSystemUserId(user.UserId) || user.Bot || user.Support)
        {
            return true;
        }

        // help.getSupport hands this account to every user, so losing it would break support chats.
        var configuredSupportUserId = options.CurrentValue.SupportUserId;

        return !string.IsNullOrEmpty(configuredSupportUserId) &&
               long.TryParse(configuredSupportUserId, out var supportUserId) &&
               supportUserId == user.UserId;
    }

    public async Task<AccountDeletionDocument> SchedulePendingAsync(long userId,
        string phoneNumber,
        string reason,
        DateTime deleteAt,
        RequestInfo requestInfo,
        CancellationToken cancellationToken = default)
    {
        var document = new AccountDeletionDocument
        {
            UserId = userId,
            PhoneNumber = phoneNumber,
            Reason = reason ?? string.Empty,
            Hash = Guid.NewGuid().ToString("N"),
            DeleteAt = deleteAt,
            RequestedAt = DateTime.UtcNow,
            RequestedByPermAuthKeyId = requestInfo.PermAuthKeyId,
            RequestedByAuthKeyId = requestInfo.AuthKeyId
        };

        await Collection.ReplaceOneAsync(p => p.UserId == userId, document,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);

        return document;
    }

    public async Task<AccountDeletionDocument?> GetPendingByUserIdAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        return await Collection.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AccountDeletionDocument?> GetPendingByHashAsync(string hash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        return await Collection.Find(p => p.Hash == hash).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task CancelPendingAsync(long userId, CancellationToken cancellationToken = default)
    {
        await Collection.DeleteOneAsync(p => p.UserId == userId, cancellationToken);
    }

    public async Task SetPhoneCodeHashAsync(long userId,
        string phoneCodeHash,
        CancellationToken cancellationToken = default)
    {
        await Collection.UpdateOneAsync(p => p.UserId == userId,
            Builders<AccountDeletionDocument>.Update
                .Set(p => p.PhoneCodeHash, phoneCodeHash)
                .Set(p => p.FailedConfirmCount, 0),
            cancellationToken: cancellationToken);
    }

    public async Task<int> IncrementFailedConfirmCountAsync(long userId,
        CancellationToken cancellationToken = default)
    {
        var document = await Collection.FindOneAndUpdateAsync<AccountDeletionDocument>(
            p => p.UserId == userId,
            Builders<AccountDeletionDocument>.Update.Inc(p => p.FailedConfirmCount, 1),
            new FindOneAndUpdateOptions<AccountDeletionDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        return document?.FailedConfirmCount ?? 0;
    }

    public async Task<AccountDeletionDocument?> ClaimNextDuePendingAsync(DateTime now,
        TimeSpan claimFor,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<AccountDeletionDocument>.Filter.And(
            Builders<AccountDeletionDocument>.Filter.Lte(p => p.DeleteAt, now),
            Builders<AccountDeletionDocument>.Filter.Or(
                Builders<AccountDeletionDocument>.Filter.Eq(p => p.ClaimedUntil, null),
                Builders<AccountDeletionDocument>.Filter.Lt(p => p.ClaimedUntil, now)));

        return await Collection.FindOneAndUpdateAsync(filter,
            Builders<AccountDeletionDocument>.Update.Set(p => p.ClaimedUntil, now.Add(claimFor)),
            new FindOneAndUpdateOptions<AccountDeletionDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task RevokeSessionAsync(long userId,
        long permAuthKeyId,
        CancellationToken cancellationToken = default)
    {
        if (permAuthKeyId == 0)
        {
            return;
        }

        var devices = await queryProcessor.ProcessAsync(new GetDeviceByUserIdQuery(userId), cancellationToken);
        var device = devices.FirstOrDefault(p => p.PermAuthKeyId == permAuthKeyId);
        if (device == null)
        {
            return;
        }

        await commandBus.PublishAsync(new UnRegisterDeviceForAuthKeyCommand(
            DeviceId.Create(device.PermAuthKeyId),
            device.PermAuthKeyId,
            device.TempAuthKeyId), cancellationToken);

        await eventBus.PublishAsync(new SessionRevokedEvent(0, userId, [device.PermAuthKeyId]));
    }

    /// <summary>
    /// Frees every username the account holds - both the legacy single username and the
    /// <a href="https://corefork.telegram.org/api/fragment">fragment</a> ones - so they can be
    /// taken by somebody else once the account is gone.
    /// </summary>
    private async Task ReleaseUserNamesAsync(IUserReadModel user, CancellationToken cancellationToken)
    {
        var userNames = new List<string>();
        if (!string.IsNullOrEmpty(user.UserName))
        {
            userNames.Add(user.UserName);
        }

        if (user.Usernames?.Count > 0)
        {
            userNames.AddRange(user.Usernames.Select(p => p.Username));
        }

        foreach (var userName in userNames
                     .Where(p => !string.IsNullOrEmpty(p))
                     .Select(p => p.ToLower())
                     .Distinct())
        {
            await commandBus.PublishAsync(new DeleteUserNameCommand(UserNameId.Create(userName)),
                cancellationToken);
        }
    }

    private async Task RevokeAllSessionsAsync(long userId, CancellationToken cancellationToken)
    {
        var devices = await queryProcessor.ProcessAsync(new GetDeviceByUserIdQuery(userId), cancellationToken);
        List<long> revokedPermAuthKeyIds = [];
        foreach (var device in devices)
        {
            await commandBus.PublishAsync(new UnRegisterDeviceForAuthKeyCommand(
                DeviceId.Create(device.PermAuthKeyId),
                device.PermAuthKeyId,
                device.TempAuthKeyId), cancellationToken);
            revokedPermAuthKeyIds.Add(device.PermAuthKeyId);
        }

        // No session survives an account deletion, so there is no current session to keep: the
        // first argument, which auth.resetAuthorizations uses to spare the caller, stays zero.
        await eventBus.PublishAsync(new SessionRevokedEvent(0, userId, revokedPermAuthKeyIds));
    }

    /// <summary>
    /// Commands carrying a <see cref="RequestInfo"/> are deduplicated on their <c>ReqMsgId</c>, so
    /// automatic deletions (the self-destruct sweeper) need a fresh one per command rather than
    /// <see cref="RequestInfo.Empty"/>. No rpc result is sent - no client request is waiting.
    /// </summary>
    private static RequestInfo CreateRequestInfo(long userId)
    {
        return RequestInfo.Empty with
        {
            UserId = userId,
            ReqMsgId = DateTime.UtcNow.Ticks,
            RequestId = Guid.NewGuid(),
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }
}
