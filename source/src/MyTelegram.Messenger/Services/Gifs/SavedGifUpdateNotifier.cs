namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// Tells the user's other sessions that their saved-GIF list changed.
///
/// <para>"Modifying the saved gifs list [...] will emit an updateSavedGifs update to other currently
/// logged in sessions, which should trigger a call to messages.getSavedGifs, to refresh the locally
/// cached list." The update carries no data — it is purely an invalidation, and the session that
/// made the change already has the RPC result, so it is excluded.</para>
/// See https://corefork.telegram.org/api/gifs#saved-gifs
/// </summary>
public interface ISavedGifUpdateNotifier
{
    Task NotifyAsync(long userId, long? excludeAuthKeyId);
}

/// <inheritdoc />
public class SavedGifUpdateNotifier(IObjectMessageSender objectMessageSender)
    : ISavedGifUpdateNotifier, ITransientDependency
{
    public Task NotifyAsync(long userId, long? excludeAuthKeyId)
    {
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateSavedGifs()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        return objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates,
            excludeAuthKeyId: excludeAuthKeyId);
    }
}
