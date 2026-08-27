namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// How many GIFs a user may keep, from
/// <a href="https://corefork.telegram.org/api/config#saved-gifs-limit-default">appConfig</a>.
///
/// <para>Every writer of the list has to agree on this number: clients truncate the list to it
/// <b>before</b> computing their hash, so handing back even one entry more than the limit means
/// <c>messages.savedGifsNotModified</c> can never match.</para>
/// </summary>
public interface ISavedGifLimitResolver
{
    Task<int> GetLimitAsync(long userId);
}

/// <inheritdoc />
public class SavedGifLimitResolver(IUserAppService userAppService, IAppConfigHelper appConfigHelper)
    : ISavedGifLimitResolver, ITransientDependency
{
    public const int DefaultLimitFallback = 200;
    public const int PremiumLimitFallback = 400;

    public async Task<int> GetLimitAsync(long userId)
    {
        var user = await userAppService.GetAsync(userId);

        return user?.Premium == true
            ? appConfigHelper.GetInt32Value("saved_gifs_limit_premium", PremiumLimitFallback)
            : appConfigHelper.GetInt32Value("saved_gifs_limit_default", DefaultLimitFallback);
    }
}
