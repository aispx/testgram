namespace MyTelegram.Messenger.Services.Caching;

public interface IUserStatusCacheAppService
{
    IUserStatus GetUserStatus(long userId);

    /// <summary>
    /// Records the presence ping. Returns whether the status other users see actually changed —
    /// clients re-send <c>account.updateStatus</c> every minute or so, and only a real change is
    /// worth an <c>updateUserStatus</c> to every contact.
    /// </summary>
    bool UpdateStatus(long userId,
        bool online);

    Task LoadFromDatabaseAsync();
}