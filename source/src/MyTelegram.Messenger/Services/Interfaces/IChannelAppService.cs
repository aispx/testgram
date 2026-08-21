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

    /// <summary>
    /// The same test as <see cref="SendRpcErrorIfNoReadAccessAsync"/> without the error: true when
    /// the channel is readable by <paramref name="userId"/>. For bulk methods that must skip an
    /// inaccessible peer rather than fail the whole request.
    /// </summary>
    Task<bool> HasReadAccessAsync(long userId, IChannelReadModel channelReadModel);

    /// <summary>
    /// Drops the cached channel and channelFull read models. Needed by handlers that write to the
    /// read model collection directly instead of going through an aggregate, since nothing else
    /// evicts the cached copy for them.
    /// </summary>
    void InvalidateCache(long channelId);
}