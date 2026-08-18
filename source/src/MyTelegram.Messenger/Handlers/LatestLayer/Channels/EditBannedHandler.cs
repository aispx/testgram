namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

/// <summary>
/// Ban/unban/kick a user in a <a href="https://corefork.telegram.org/api/channel">supergroup/channel</a>.
/// Possible errors
/// Code Type Description
/// 406 BANNED_RIGHTS_INVALID You provided some invalid flags in the banned rights.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PARTICIPANT_ID_INVALID The specified participant ID is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_ADMIN_INVALID You're not an admin.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.editBanned"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditBannedHandler(IPeerHelper peerHelper, ICommandBus commandBus, IChannelAppService channelAppService, IChannelAdminRightsChecker channelAdminRightsChecker, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestEditBanned, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditBanned obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            var channel = peerHelper.GetChannel(obj.Channel);
            var peer = peerHelper.GetPeer(obj.Participant);
            var channelReadModel = await channelAppService.GetAsync(channel.PeerId);
            await channelAdminRightsChecker.CheckAdminRightAsync(inputChannel.ChannelId, input.UserId, p => p.BanUsers && peer.PeerId != channelReadModel.CreatorId, RpcErrors.RpcErrors400.ChatAdminRequired);

            if (obj.BannedRights.UntilDate < 0)
            {
                RpcErrors.RpcErrors400.UntilDateInvalid.ThrowRpcError();
            }

            // Only the owner may restrict another admin.
            if (channelReadModel.CreatorId != input.UserId &&
                channelReadModel.AdminList.Any(p => p.UserId == peer.PeerId))
            {
                RpcErrors.RpcErrors400.UserAdminInvalid.ThrowRpcError();
            }

            // send_plain is the modern flag; the legacy send_messages flag is kept in sync so older
            // clients and the stored flags agree. SetBit returns a new value, it does not mutate.
            if (obj.BannedRights.SendPlain)
            {
                obj.BannedRights.SendMessages = true;
                obj.BannedRights.Flags = obj.BannedRights.Flags.SetBit(1);
            }

            // A restriction shorter than 30 seconds or longer than 366 days means "forever".
            // See https://corefork.telegram.org/constructor/chatBannedRights
            var untilDate = ChatBannedRights.NormalizeUntilDate(obj.BannedRights.UntilDate, CurrentDate);
            obj.BannedRights.UntilDate = untilDate;
            var bannedRights = ChatBannedRights.FromValue(obj.BannedRights.Flags, untilDate);

            var memberUserId = peer.PeerId;
            if (peer.PeerType == PeerType.Channel)
            {
                var sendAsChannelReadModel = await channelAppService.GetAsync(peer.PeerId);
                memberUserId = sendAsChannelReadModel.CreatorId;
            }

            // Get previous state for admin log
            var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channel.PeerId, peer.PeerId));
            var prevParticipant = channelMember != null && channelMember.BannedRights != 0
                ? new MyTelegram.Schema.TChannelParticipantBanned
                {
                    Peer = new TPeerUser { UserId = peer.PeerId },
                    BannedRights = new TChatBannedRights
                    {
                        Flags = channelMember.BannedRights,
                        UntilDate = channelMember.UntilDate
                    },
                    KickedBy = channelMember.KickedBy,
                    Date = channelMember.Date
                }
                : (MyTelegram.Schema.IChannelParticipant)new MyTelegram.Schema.TChannelParticipant
                {
                    UserId = peer.PeerId,
                    Date = channelMember?.Date ?? CurrentDate
                };

            var newParticipant = new MyTelegram.Schema.TChannelParticipantBanned
            {
                Peer = new TPeerUser { UserId = peer.PeerId },
                BannedRights = obj.BannedRights,
                KickedBy = input.UserId,
                Date = CurrentDate
            };

            var command = new EditBannedCommand(ChannelMemberId.Create(channel.PeerId, memberUserId), input.ToRequestInfo(), input.UserId, channel.PeerId, memberUserId, bannedRights);
            await commandBus.PublishAsync(command);

            // Logged only once the command went through, so a rejected change leaves no admin log entry.
            await Helpers.AdminLogHelper.LogEditBanned(mongoDatabase, channel.PeerId, input.UserId, prevParticipant, newParticipant);
            return null!;
        }

        throw new NotImplementedException();
    }
}