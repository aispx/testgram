namespace MyTelegram.Messenger.Services.Impl;

public class ChannelAppService(IQueryProcessor queryProcessor,
    IReadModelCacheHelper<IChannelReadModel> channelReadModelCacheHelper,
    IRpcErrorHelper rpcErrorHelper,
    IChatInvitePeekService chatInvitePeekService,
    IReadModelCacheHelper<IChannelFullReadModel> channelFullReadModelCacheHelper) :
    ReadModelWithCacheAppService<IChannelReadModel>(channelReadModelCacheHelper),
    IChannelAppService, ITransientDependency
{
    public Task<IChannelFullReadModel?> GetChannelFullAsync(long channelId)
    {
        return channelFullReadModelCacheHelper.GetOrCreateAsync(channelId,
            () => queryProcessor.ProcessAsync(new GetChannelFullByIdQuery(channelId)), p => p.Id);
    }

    public void InvalidateCache(long channelId)
    {
        channelReadModelCacheHelper.RemoveById(channelId);
        channelFullReadModelCacheHelper.RemoveById(channelId);
    }

    protected override Task<IChannelReadModel?> GetReadModelAsync(long id)
    {
        return queryProcessor.ProcessAsync(new GetChannelByIdQuery(id));
    }

    protected override string GetReadModelId(IChannelReadModel readModel) => readModel.Id;

    protected override long GetReadModelInt64Id(IChannelReadModel readModel) => readModel.ChannelId;
    protected override Task<IChannelReadModel?> CreateNonExistsReadModelAsync(long id)
    {
        return Task.FromResult<IChannelReadModel?>(null);
    }

    protected override Task<IReadOnlyCollection<IChannelReadModel>> GetReadModelListAsync(List<long> ids)
    {
        return queryProcessor.ProcessAsync(new GetChannelByChannelIdListQuery(ids));
    }

    public async Task<bool> IsChannelMemberAsync(long userId, long channelId)
    {
        var channelMemberReadModel = await queryProcessor
            .ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, userId));

        return channelMemberReadModel != null;
    }



    public async Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, long channelId)
    {
        var channelReadModel = await GetAsync(channelId);
        return await SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel);
    }

    public async Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, IChannelReadModel channelReadModel)
    {
        if (RequiresMembership(channelReadModel))
        {
            if (!await IsChannelMemberAsync(input.UserId, channelReadModel.ChannelId))
            {
                await rpcErrorHelper.ThrowRpcErrorAsync(input, RpcErrors.RpcErrors400.ChannelPrivate);

                return true;
            }
        }

        return false;
    }

    public async Task<bool> SendRpcErrorIfNoReadAccessAsync(IRequestInput input, IChannelReadModel channelReadModel)
    {
        if (await HasReadAccessAsync(input.UserId, channelReadModel))
        {
            return false;
        }

        await rpcErrorHelper.ThrowRpcErrorAsync(input, RpcErrors.RpcErrors400.ChannelPrivate);

        return true;
    }

    public async Task<bool> HasReadAccessAsync(long userId, IChannelReadModel channelReadModel)
    {
        if (!RequiresMembership(channelReadModel))
        {
            return true;
        }

        if (await IsChannelMemberAsync(userId, channelReadModel.ChannelId))
        {
            return true;
        }

        // messages.checkChatInvite may hand a non-member temporary read-only access to the chat,
        // see https://corefork.telegram.org/constructor/chatInvitePeek
        return await chatInvitePeekService.HasActivePeekAsync(userId, channelReadModel.ChannelId);
    }

    /// <summary>
    /// Only private chats keep non-members out: a public one, a discussion group of a channel and a
    /// broadcast channel can all be read by anybody who has the peer.
    /// </summary>
    private static bool RequiresMembership(IChannelReadModel channelReadModel)
    {
        return string.IsNullOrEmpty(channelReadModel.UserName) &&
               channelReadModel is { Broadcast: false, LinkedChatId: null } &&
               !channelReadModel.IsMonoforum;
    }
}