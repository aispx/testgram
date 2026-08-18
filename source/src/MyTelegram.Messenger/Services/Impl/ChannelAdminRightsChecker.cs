namespace MyTelegram.Messenger.Services.Impl;

public class ChannelAdminRightsChecker(IQueryProcessor queryProcessor, IChannelAppService channelAppService) : IChannelAdminRightsChecker, ITransientDependency
{
    public async Task<bool> HasChatAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc)
    {
        var channelReadModel = await channelAppService.GetAsync(channelId);
        if (channelReadModel.CreatorId == userId)
        {
            return true;
        }

        var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == userId);
        if (admin == null)
        {
            return false;
        }

        return checkAdminRightsFunc(admin.AdminRights);
    }

    public async Task CheckAdminRightAsync(IInputChannel channel, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null)
    {
        var channelId = GetChannelId(channel);
        if (channelId == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        await CheckAdminRightAsync(channelId!.Value, userId, checkAdminRightsFunc, rpcError);
    }

    /// <summary>
    /// Resolves the channel id of an <see cref="IInputChannel"/>, or null when the constructor carries
    /// no channel at all (<c>inputChannelEmpty</c>, used by channels.setDiscussionGroup to unlink) or is
    /// of an unknown kind. Callers decide which of the two is an error in their context.
    /// </summary>
    public long? GetChannelId(IInputChannel channel)
    {
        return channel switch
        {
            TInputChannel inputChannel => inputChannel.ChannelId,
            TInputChannelFromMessage inputChannelFromMessage => inputChannelFromMessage.ChannelId,
            _ => null
        };
    }

    public async Task CheckAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null)
    {
        if (!await HasChatAdminRightAsync(channelId, userId, checkAdminRightsFunc))
        {
            (rpcError ?? RpcErrors.RpcErrors400.ChatAdminRequired).ThrowRpcError();
        }
    }

    public async Task ThrowIfNotChannelOwnerAsync(IInputChannel channel, long userId)
    {
        var channelId = GetChannelId(channel);
        if (channelId == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        await ThrowIfNotChannelOwnerAsync(channelId!.Value, userId);
    }

    public async Task ThrowIfNotChannelOwnerAsync(long channelId, long userId)
    {
        var channelReadModel = await channelAppService.GetAsync((long?)channelId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        if (channelReadModel!.CreatorId != userId)
        {
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        }
    }
}