using MyTelegram.Schema.Users;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Obtain a list of similarly themed bots, selected based on similarities in their subscriber bases, see <a href="https://corefork.telegram.org/api/recommend">here »</a> for more info.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getBotRecommendations"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotRecommendationsHandler(
    IRecommendationAppService recommendationAppService,
    IUserAppService userAppService,
    IUserConverterService userConverterService,
    IAccessHashHelper2 accessHashHelper,
    IAppConfigHelper appConfigHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetBotRecommendations, MyTelegram.Schema.Users.IUsers>
{
    private const int DefaultLimitFallback = 10;
    private const int PremiumLimitFallback = 100;

    protected override async Task<MyTelegram.Schema.Users.IUsers> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetBotRecommendations obj)
    {
        if (obj.Bot is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            throw new InvalidOperationException();
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputUser.UserId, inputUser.AccessHash, AccessHashType.User);

        var botReadModel = await userAppService.GetAsync(inputUser.UserId);
        if (botReadModel is not { Bot: true } || botReadModel.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var defaultLimit = appConfigHelper.GetInt32Value("recommended_channels_limit_default", DefaultLimitFallback);
        var premiumLimit = appConfigHelper.GetInt32Value("recommended_channels_limit_premium", PremiumLimitFallback);

        var selfUserReadModel = await userAppService.GetAsync(input.UserId);
        if (selfUserReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var isPremium = selfUserReadModel!.Premium;
        var limit = isPremium ? premiumLimit : Math.Min(defaultLimit, premiumLimit);

        // The total is capped at the premium limit so the "unlock N more with Premium" hint clients
        // compute from count - users.Count cannot promise bots Premium would not deliver either.
        var recommendation = await recommendationAppService.GetSimilarBotIdsAsync(input.UserId, inputUser.UserId, limit, premiumLimit);
        if (recommendation.Ids.Count == 0)
        {
            return new TUsers { Users = new TVector<IUser>() };
        }

        var users = await userConverterService.GetUserListAsync(input, recommendation.Ids, layer: input.Layer);

        if (isPremium)
        {
            return new TUsers { Users = [.. users] };
        }

        // Non-premium accounts get a truncated list, with the real total in count so clients can show
        // the "unlock more with Premium" hint. See https://corefork.telegram.org/api/recommend
        return new TUsersSlice
        {
            Count = recommendation.TotalCount,
            Users = [.. users]
        };
    }
}