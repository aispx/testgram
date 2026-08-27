namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// The limits of the <a href="https://corefork.telegram.org/api/folders">folders</a> surface, every one of
/// them Premium-dependent and every one of them advertised through <c>help.getAppConfig</c>. The numbers
/// have to agree with what the client was told, because clients enforce the same limits locally and show a
/// "limit reached" sheet keyed on the error the server answers with.
/// </summary>
public interface IDialogFilterLimitResolver
{
    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#dialog-filters-limit-default">dialog_filters_limit</a>
    /// — how many folders a user may have, not counting <c>dialogFilterDefault</c>.
    /// </summary>
    Task<int> GetFilterLimitAsync(long userId);

    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#dialog-filters-chats-limit-default">dialog_filters_chats_limit</a>
    /// — how many chats one folder may name explicitly.
    /// </summary>
    Task<int> GetChatsPerFilterLimitAsync(long userId);

    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#dialogs-folder-pinned-limit-default">dialogs_folder_pinned_limit</a>
    /// — how many chats may be pinned inside one folder.
    /// </summary>
    Task<int> GetPinnedPerFilterLimitAsync(long userId);

    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#chatlists-joined-limit-default">chatlists_joined_limit</a>
    /// — how many shareable folders a user may have imported.
    /// </summary>
    Task<int> GetJoinedChatlistLimitAsync(long userId);

    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#chatlist-invites-limit-default">chatlist_invites_limit</a>
    /// — how many links one user may export per folder.
    /// </summary>
    Task<int> GetChatlistInvitesLimitAsync(long userId);
}

/// <inheritdoc />
public class DialogFilterLimitResolver(IUserAppService userAppService, IAppConfigHelper appConfigHelper)
    : IDialogFilterLimitResolver, ITransientDependency
{
    public const int FilterLimitFallback = 10;
    public const int FilterPremiumLimitFallback = 30;
    public const int ChatsPerFilterLimitFallback = 100;
    public const int ChatsPerFilterPremiumLimitFallback = 200;
    public const int PinnedPerFilterLimitFallback = 100;
    public const int PinnedPerFilterPremiumLimitFallback = 200;
    public const int JoinedChatlistLimitFallback = 2;
    public const int JoinedChatlistPremiumLimitFallback = 20;
    public const int ChatlistInvitesLimitFallback = 3;
    public const int ChatlistInvitesPremiumLimitFallback = 100;

    public Task<int> GetFilterLimitAsync(long userId)
    {
        return GetLimitAsync(userId, "dialog_filters_limit_premium", FilterPremiumLimitFallback,
            "dialog_filters_limit_default", FilterLimitFallback);
    }

    public Task<int> GetChatsPerFilterLimitAsync(long userId)
    {
        return GetLimitAsync(userId, "dialog_filters_chats_limit_premium", ChatsPerFilterPremiumLimitFallback,
            "dialog_filters_chats_limit_default", ChatsPerFilterLimitFallback);
    }

    public Task<int> GetPinnedPerFilterLimitAsync(long userId)
    {
        return GetLimitAsync(userId, "dialogs_folder_pinned_limit_premium", PinnedPerFilterPremiumLimitFallback,
            "dialogs_folder_pinned_limit_default", PinnedPerFilterLimitFallback);
    }

    public Task<int> GetJoinedChatlistLimitAsync(long userId)
    {
        return GetLimitAsync(userId, "chatlists_joined_limit_premium", JoinedChatlistPremiumLimitFallback,
            "chatlists_joined_limit_default", JoinedChatlistLimitFallback);
    }

    public Task<int> GetChatlistInvitesLimitAsync(long userId)
    {
        return GetLimitAsync(userId, "chatlist_invites_limit_premium", ChatlistInvitesPremiumLimitFallback,
            "chatlist_invites_limit_default", ChatlistInvitesLimitFallback);
    }

    private async Task<int> GetLimitAsync(long userId, string premiumKey, int premiumFallback, string defaultKey,
        int defaultFallback)
    {
        var user = await userAppService.GetAsync(userId);

        return user?.Premium == true
            ? appConfigHelper.GetInt32Value(premiumKey, premiumFallback)
            : appConfigHelper.GetInt32Value(defaultKey, defaultFallback);
    }
}
