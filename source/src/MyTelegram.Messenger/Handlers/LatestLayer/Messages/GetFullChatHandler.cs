using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get full info about a <a href="https://corefork.telegram.org/api/channel#basic-groups">basic group</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFullChat"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetFullChatHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChatConverterService chatConverterService,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase,
    IPinnedMessageResolver pinnedMessageResolver)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFullChat, MyTelegram.Schema.Messages.IChatFull>
{
    protected override async Task<MyTelegram.Schema.Messages.IChatFull> HandleCoreAsync(IRequestInput input, RequestGetFullChat obj)
    {
        var peerType = peerHelper.GetPeerType(obj.ChatId);
        switch (peerType)
        {
            case PeerType.Channel:
            {
                var channel = await channelAppService.GetAsync(obj.ChatId);
                var channelFull = await queryProcessor.ProcessAsync(new GetChannelFullByIdQuery(obj.ChatId));
                var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(obj.ChatId, input.UserId));
                var peerNotifySettings = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(PeerNotifySettingsId.Create(input.UserId, PeerType.Channel, obj.ChatId).Value), CancellationToken.None);
                var photoReadModel = await photoAppService.GetAsync(channel.PhotoId);
                var chatFull = chatConverterService.ToChannelFull(input, channel, photoReadModel, channelFull!, channelMember, peerNotifySettings, null, input.Layer);
                var activeCall = await GroupCallStateHelper.FindActivePeerCallAsync(
                    mongoDatabase,
                    obj.ChatId,
                    PeerType.Channel);
                if (activeCall != null)
                {
                    GroupCallStateHelper.ApplyActiveCallToChatFull(chatFull, activeCall, input.UserId);
                }

                // ChannelFullReadModel.PinnedMsgId is never written, so the latest pinned message has to
                // be resolved live here just like channels.getFullChannel does.
                var pinnedMsgId = await pinnedMessageResolver.GetChannelPinnedMsgIdAsync(obj.ChatId);
                if (pinnedMsgId.HasValue && chatFull.FullChat is TChannelFull tChannelFull)
                {
                    tChannelFull.PinnedMsgId = pinnedMsgId.Value;
                }

                return chatFull;
            }
        }

        throw new NotImplementedException($"Not supported peer type {peerType},chatId={obj.ChatId}");
    }
}
