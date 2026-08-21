namespace MyTelegram.Messenger.Services;

/// <summary>
/// Resolves the <c>input*FromMessage</c> constructors — <c>inputUserFromMessage</c>,
/// <c>inputChannelFromMessage</c>, <c>inputPeerUserFromMessage</c> and
/// <c>inputPeerChannelFromMessage</c>.
/// <para>
/// A client that only ever saw a peer through a <a href="https://corefork.telegram.org/api/min">min
/// constructor</a> has no usable access hash for it, so instead of an access hash it cites the
/// context the peer was seen in: a container peer plus a message id. The access check therefore is
/// not "does this hash match" but "is the cited message one the caller can read, and does the
/// target peer really appear in it".
/// </para>
/// <para>
/// Without that check the constructors are a hole in access-hash validation: the caller picks the
/// target id themselves, and every other field is decoration. See
/// https://corefork.telegram.org/api/peers#access-hash
/// </para>
/// <para>
/// The mirror image of this check is <see cref="IMinConstructorReducer"/>, which decides which peers
/// go out as <c>min</c> in the first place. Both sides consult <see cref="MessagePeerReferences"/>, so
/// a peer is only ever reduced to <c>min</c> when the citation the client will build for it is one
/// this resolver accepts back.
/// </para>
/// </summary>
public interface IFromMessagePeerResolver
{
    /// <summary>
    /// Validates the context of an <c>inputUserFromMessage</c>/<c>inputPeerUserFromMessage</c> and
    /// returns the user id it points at.
    /// </summary>
    /// <exception cref="RpcException">
    /// FROM_MESSAGE_BOT_DISABLED when the caller is a bot, MSG_ID_INVALID when the cited message is
    /// not readable by the caller, PEER_ID_INVALID when the user does not appear in it.
    /// </exception>
    Task<long> ResolveUserIdAsync(IRequestInput input, IInputPeer? container, int msgId, long userId);

    /// <summary>
    /// Validates the context of an <c>inputChannelFromMessage</c>/<c>inputPeerChannelFromMessage</c>
    /// and returns the channel id it points at.
    /// </summary>
    /// <exception cref="RpcException">
    /// FROM_MESSAGE_BOT_DISABLED when the caller is a bot, MSG_ID_INVALID when the cited message is
    /// not readable by the caller, CHANNEL_INVALID when the channel does not appear in it.
    /// </exception>
    Task<long> ResolveChannelIdAsync(IRequestInput input, IInputPeer? container, int msgId, long channelId);
}

public sealed class FromMessagePeerResolver(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService)
    : IFromMessagePeerResolver, ITransientDependency
{
    public async Task<long> ResolveUserIdAsync(IRequestInput input, IInputPeer? container, int msgId, long userId)
    {
        var message = await LoadCitedMessageAsync(input, container, msgId);

        if (userId <= 0 || !MessagePeerReferences.ReferencesUser(message, userId))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        return userId;
    }

    public async Task<long> ResolveChannelIdAsync(IRequestInput input, IInputPeer? container, int msgId, long channelId)
    {
        var message = await LoadCitedMessageAsync(input, container, msgId);

        if (channelId <= 0 || !MessagePeerReferences.ReferencesChannel(message, channelId))
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        return channelId;
    }

    /// <summary>
    /// Loads the message the caller cites as context, after proving they may read it. Messages are
    /// stored per dialog copy: a channel message lives under the channel id, a private one under
    /// each participant's own id, so looking the message up by the caller's own owner peer id is
    /// itself the access check for private chats.
    /// </summary>
    private async Task<IMessageReadModel> LoadCitedMessageAsync(IRequestInput input, IInputPeer? container, int msgId)
    {
        // Bots never receive min constructors, so they have no reason to send fromMessage ones.
        if (peerHelper.IsBotUser(input.UserId))
        {
            RpcErrors.RpcErrors400.FromMessageBotDisabled.ThrowRpcError();
        }

        if (msgId <= 0)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        if (container == null || container is TInputPeerEmpty)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var containerPeer = peerHelper.GetPeer(container!, input.UserId);
        if (containerPeer == null || containerPeer.PeerType == PeerType.Chat)
        {
            // Testgram stores every group as a channel, so a basic-group container can never hold
            // a message here.
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var ownerPeerId = containerPeer!.PeerType == PeerType.Channel ? containerPeer.PeerId : input.UserId;

        if (containerPeer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync((long?)containerPeer.PeerId);
            channelReadModel.ThrowExceptionIfChannelDeleted();
            await channelAppService.SendRpcErrorIfNoReadAccessAsync(input, channelReadModel!);
        }

        var message = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, msgId));
        if (message == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        return message!;
    }
}
