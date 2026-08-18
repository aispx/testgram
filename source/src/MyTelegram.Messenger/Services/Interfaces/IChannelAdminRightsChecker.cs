namespace MyTelegram.Messenger.Services.Interfaces;

public interface IChannelAdminRightsChecker
{
    Task<bool> HasChatAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc);

    Task CheckAdminRightAsync(IInputChannel channel, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null);
    Task CheckAdminRightAsync(long channelId, long userId, Func<ChatAdminRights, bool> checkAdminRightsFunc, RpcError? rpcError = null);

    Task ThrowIfNotChannelOwnerAsync(IInputChannel channel, long userId);
    Task ThrowIfNotChannelOwnerAsync(long channelId, long userId);

    /// <summary>
    /// Channel id of an <see cref="IInputChannel"/>, or null for <c>inputChannelEmpty</c> and unknown
    /// constructors.
    /// </summary>
    long? GetChannelId(IInputChannel channel);
}