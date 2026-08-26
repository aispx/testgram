namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// How many stickers a user may keep in each list.
///
/// <para>Every writer of a list has to agree with the number the client sees, because clients truncate a
/// list to the limit <b>before</b> hashing it: handing back even one entry more than the limit means the
/// <c>*NotModified</c> answer can never match again.</para>
/// </summary>
public interface IStickerLimitResolver
{
    /// <summary>
    /// <a href="https://corefork.telegram.org/api/config#stickers-faved-limit-default">stickers_faved_limit</a>,
    /// which is Premium-dependent.
    /// </summary>
    Task<int> GetFavedLimitAsync(long userId);

    /// <summary>
    /// <c>config.stickers_recent_limit</c>. Unlike the favourites limit this one lives in
    /// <c>help.getConfig</c>, not in appConfig, and is the same for everyone.
    /// </summary>
    int GetRecentLimit();

    /// <summary>
    /// How many non-archived sets of one kind a user may have before the server starts archiving the
    /// oldest. Telegram does not publish this one in appConfig at all — the client only learns of it by
    /// receiving <c>messages.stickerSetInstallResultArchive</c>.
    /// </summary>
    int GetInstalledLimit();
}

/// <inheritdoc />
public class StickerLimitResolver(IUserAppService userAppService, IAppConfigHelper appConfigHelper)
    : IStickerLimitResolver, ITransientDependency
{
    public const int FavedLimitFallback = 5;
    public const int FavedPremiumLimitFallback = 10;

    /// <summary>Matches what <c>ConfigConverter</c> advertises as <c>config.stickers_recent_limit</c>.</summary>
    public const int RecentLimitFallback = 200;

    /// <summary>How many sets the official server keeps unarchived.</summary>
    public const int InstalledLimitFallback = 200;

    public async Task<int> GetFavedLimitAsync(long userId)
    {
        var user = await userAppService.GetAsync(userId);

        return user?.Premium == true
            ? appConfigHelper.GetInt32Value("stickers_faved_limit_premium", FavedPremiumLimitFallback)
            : appConfigHelper.GetInt32Value("stickers_faved_limit_default", FavedLimitFallback);
    }

    public int GetRecentLimit()
    {
        return appConfigHelper.GetInt32Value("stickers_recent_limit", RecentLimitFallback);
    }

    public int GetInstalledLimit()
    {
        return appConfigHelper.GetInt32Value("stickers_installed_limit", InstalledLimitFallback);
    }
}
