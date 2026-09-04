namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// Tells the user's other sessions that their saved notification sounds changed.
///
/// <para>"The client will receive an <c>updateSavedRingtones</c> update if the list is modified by the
/// user on other clients, which should trigger a call to <c>account.getSavedRingtones</c>." The update
/// carries no data — it is purely an invalidation, and the session that made the change already has the
/// RPC result, so it is excluded.</para>
///
/// <para>The exclusion is by <b>permanent</b> auth key id: with PFS the request arrives over a temporary
/// key, so excluding the temporary id fails to exclude the session that wrote the change and it is told
/// about its own edit.</para>
/// See https://corefork.telegram.org/api/ringtones#getting-notification-sounds
/// </summary>
public interface ISavedRingtoneUpdateNotifier
{
    Task NotifyAsync(long userId, long? excludeAuthKeyId);
}

/// <inheritdoc />
public class SavedRingtoneUpdateNotifier(IObjectMessageSender objectMessageSender)
    : ISavedRingtoneUpdateNotifier, ITransientDependency
{
    public Task NotifyAsync(long userId, long? excludeAuthKeyId)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateSavedRingtones()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        return objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
            excludeAuthKeyId: excludeAuthKeyId);
    }
}
