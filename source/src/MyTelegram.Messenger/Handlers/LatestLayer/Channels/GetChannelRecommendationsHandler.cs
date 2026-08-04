namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Obtain a list of similarly themed public channels, selected based on similarities in their <strong>subscriber bases</strong>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getChannelRecommendations"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelRecommendationsHandler(
    IRecommendationAppService recommendationAppService,
    IChannelAppService channelAppService,
    IChatConverterService chatConverterService,
    IUserAppService userAppService,
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IAppConfigHelper appConfigHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetChannelRecommendations, MyTelegram.Schema.Messages.IChats>
{
    private const int DefaultLimitFallback = 10;
    private const int PremiumLimitFallback = 100;

    protected override async Task<MyTelegram.Schema.Messages.IChats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestGetChannelRecommendations obj)
    {
        long? sourceChannelId = null;
        if (obj.Channel != null)
        {
            if (obj.Channel is not TInputChannel inputChannel)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                throw new InvalidOperationException();
            }

            await accessHashHelper.CheckAccessHashAsync(input, inputChannel.ChannelId, inputChannel.AccessHash, AccessHashType.Channel);

            var channelReadModel = await channelAppService.GetAsync(inputChannel.ChannelId);
            if (channelReadModel == null || channelReadModel.IsDeleted)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            // Only broadcast channels have a comparable subscriber base.
            if (!channelReadModel!.Broadcast)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel))
            {
                return EmptyResult();
            }

            sourceChannelId = inputChannel.ChannelId;
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

        // The total is capped at the premium limit: clients render "count - chats.Count" as the
        // "unlock N more with Premium" hint, so it must not promise more than Premium would deliver.
        var recommendation = await recommendationAppService.GetSimilarChannelIdsAsync(input.UserId, sourceChannelId, limit, premiumLimit);
        if (recommendation.Ids.Count == 0)
        {
            return EmptyResult();
        }

        var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, recommendation.Ids));
        var channels = await chatConverterService.GetChannelListAsync(input, recommendation.Ids, channelMemberReadModels, layer: input.Layer);

        // Always a slice, for premium accounts too: the count doubles as the "show all N" total in the
        // panel header. For premium it equals the number of items, so no upsell is shown.
        // See https://corefork.telegram.org/api/recommend
        return new TChatsSlice
        {
            Count = recommendation.TotalCount,
            Chats = [.. channels]
        };
    }

    private static MyTelegram.Schema.Messages.IChats EmptyResult()
    {
        return new TChatsSlice { Count = 0, Chats = new TVector<IChat>() };
    }
}