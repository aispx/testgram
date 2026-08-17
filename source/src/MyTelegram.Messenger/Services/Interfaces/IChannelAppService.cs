namespace MyTelegram.Messenger.Services.Interfaces;

public interface IChannelAppService : IReadModelWithCacheAppService<IChannelReadModel>
{
    Task<IChannelFullReadModel?> GetChannelFullAsync(long channelId);
    Task<bool> IsChannelMemberAsync(long userId, long channelId);
    Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, IChannelReadModel channelReadModel);
    Task<bool> SendRpcErrorIfNotChannelMemberAsync(IRequestInput input, long channelId);

    /// <summary>
    /// Like <see cref="SendRpcErrorIfNotChannelMemberAsync(IRequestInput, IChannelReadModel)"/>, but
    /// also lets through a user holding a <c>chatInvitePeek</c> window, who may read the chat
    /// without having joined it. Only for read-only methods.
    /// </summary>
    Task<bool> SendRpcErrorIfNoReadAccessAsync(IRequestInput input, IChannelReadModel channelReadModel);
}