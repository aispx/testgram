namespace MyTelegram.Messenger.Services;

/// <summary>
/// The name and username values a caller already knows to be current, used when the read model may
/// not have caught up yet.
/// </summary>
public sealed record UserNameSnapshot(string FirstName, string? LastName, string? UserName);

/// <summary>
/// Pushes <c>updateUserName</c> after a username list changed through <c>account.updateUsername</c>,
/// <c>account.toggleUsername</c> or <c>account.reorderUsernames</c>.
/// <para>
/// The <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>
/// is meant to be kept fresh reactively: without this update the caller's other sessions, and
/// everybody holding them in a contact list, keep serving a stale username until something else
/// forces a refetch.
/// </para>
/// </summary>
public interface IUsernameUpdateNotifier
{
    /// <summary>
    /// Re-reads the user and delivers <c>updateUserName</c> to their other sessions and to everyone
    /// who has them as a contact. The session that made the call is skipped: it already learns the
    /// outcome from the rpc result and applies the change locally, as the API docs require.
    /// </summary>
    Task NotifyUserNameChangedAsync(IRequestInput input, long userId);

    /// <inheritdoc cref="NotifyUserNameChangedAsync(IRequestInput, long)"/>
    /// <param name="snapshot">
    /// Values that win over the read model. Domain event handlers run alongside the read model
    /// update, so they pass what the event carried rather than risk announcing the previous name.
    /// </param>
    Task NotifyUserNameChangedAsync(long userId, long? excludeAuthKeyId, UserNameSnapshot? snapshot = null);
}

public sealed class UsernameUpdateNotifier(
    IUserAppService userAppService,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender)
    : IUsernameUpdateNotifier, ITransientDependency
{
    public Task NotifyUserNameChangedAsync(IRequestInput input, long userId)
    {
        return NotifyUserNameChangedAsync(userId, input.AuthKeyId);
    }

    public async Task NotifyUserNameChangedAsync(long userId, long? excludeAuthKeyId,
        UserNameSnapshot? snapshot = null)
    {
        // The username handlers write straight to the read model collection, so the cached copy
        // must be dropped before anything reads the user again.
        userAppService.InvalidateCache(userId);

        var userReadModel = await userAppService.GetAsync((long?)userId);
        if (userReadModel == null && snapshot == null)
        {
            return;
        }

        var update = new TUpdateUserName
        {
            UserId = userId,
            FirstName = snapshot?.FirstName ?? userReadModel!.FirstName,
            LastName = snapshot?.LastName ?? userReadModel?.LastName ?? string.Empty,
            Usernames = BuildUsernames(userReadModel, snapshot)
        };

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
            excludeAuthKeyId: excludeAuthKeyId);

        var contactUserIds = await queryProcessor.ProcessAsync(
            new GetContactSelfUserIdListByTargetUserIdQuery(userId));

        foreach (var contactUserId in contactUserIds.Where(p => p != userId).Distinct())
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, contactUserId), updates);
        }
    }

    /// <summary>
    /// The stored username list, with the editable (non-Fragment) entry replaced by the one the
    /// snapshot reports. Fragment usernames only ever change through methods that notify on their
    /// own, so taking them from the read model is safe even when it lags behind by an instant.
    /// </summary>
    private static TVector<IUsername> BuildUsernames(IUserReadModel? userReadModel, UserNameSnapshot? snapshot)
    {
        var usernames = new TVector<IUsername>();

        foreach (var usernameInfo in userReadModel?.Usernames ?? [])
        {
            if (snapshot != null && usernameInfo.Editable)
            {
                continue;
            }

            usernames.Add(new TUsername
            {
                Username = usernameInfo.Username,
                Editable = usernameInfo.Editable,
                Active = usernameInfo.Active
            });
        }

        if (snapshot == null)
        {
            return usernames;
        }

        if (!string.IsNullOrEmpty(snapshot.UserName))
        {
            // The editable username always comes first, as the primary one.
            usernames.Insert(0, new TUsername { Username = snapshot.UserName, Editable = true, Active = true });
        }

        return usernames;
    }
}
