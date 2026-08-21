namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Get info about <a href="https://corefork.telegram.org/api/channel">channels/supergroups</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getChannels"/> </c></para>
/// </summary>
/// <remarks>
/// One of the three bulk methods clients use to refresh their
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>.
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelsHandler(
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IFromMessagePeerResolver fromMessagePeerResolver)
    : RpcResultObjectHandler<RequestGetChannels, IChats>
{
    protected override async Task<IChats> HandleCoreAsync(IRequestInput input, RequestGetChannels obj)
    {
        var channelIds = new List<long>();

        foreach (var inputChannel in obj.Id)
        {
            switch (inputChannel)
            {
                case TInputChannel tInputChannel:
                    await accessHashHelper.CheckAccessHashAsync(input, tInputChannel.ChannelId,
                        tInputChannel.AccessHash, AccessHashType.Channel);
                    channelIds.Add(tInputChannel.ChannelId);
                    break;

                // A channel only ever seen through a min constructor has no usable access hash, so
                // the caller cites the message it was seen in instead.
                // See https://corefork.telegram.org/api/min
                case TInputChannelFromMessage inputChannelFromMessage:
                    channelIds.Add(await fromMessagePeerResolver.ResolveChannelIdAsync(input,
                        inputChannelFromMessage.Peer, inputChannelFromMessage.MsgId,
                        inputChannelFromMessage.ChannelId));
                    break;

                // inputChannelEmpty and anything unknown cannot name a channel.
                default:
                    RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                    break;
            }
        }

        channelIds = channelIds.Distinct().ToList();
        if (channelIds.Count == 0)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
        var channels = await chatConverterService.GetChannelListAsync(input, channelIds, channelMemberReadModels, layer: input.Layer);

        return new TChats
        {
            Chats = [.. channels]
        };
    }
}
