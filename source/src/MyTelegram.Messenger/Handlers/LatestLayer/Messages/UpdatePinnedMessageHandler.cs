// ReSharper disable All
using GetSimpleMessageListQuery = MyTelegram.Queries.GetSimpleMessageListQuery;
using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Pin a message
/// Possible errors
/// Code Type Description
/// 400 BOT_ONESIDE_NOT_AVAIL Bots can't pin messages in PM just for themselves.
/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.
/// 400 BUSINESS_PEER_INVALID Messages can't be set to the specified peer through the current <a href="https://corefork.telegram.org/api/business#connected-bots">business connection</a>.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_INVALID Invalid chat.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PIN_RESTRICTED You can't pin messages.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.updatePinnedMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class UpdatePinnedMessageHandler(ICommandBus commandBus, IPeerHelper peerHelper, IQueryProcessor queryProcessor, IPinRightsChecker pinRightsChecker, IMongoDatabase mongoDatabase, IMessageConverterService messageConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUpdatePinnedMessage, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestUpdatePinnedMessage obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType == PeerType.Empty)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // pm_oneside only means anything in a one-to-one chat, and a bot has no local-only pin at all.
        var pmOneSide = obj.PmOneside && peer!.PeerType is PeerType.User or PeerType.Self;
        if (obj.PmOneside && peerHelper.IsBotUser(input.UserId))
        {
            RpcErrors.RpcErrors400.BotOnesideNotAvail.ThrowRpcError();
        }

        await pinRightsChecker.CheckPinRightsAsync(input, peer!);

        var messageItems = await queryProcessor.ProcessAsync(new GetSimpleMessageListQuery(input.UserId, peer!, [obj.Id], null, !pmOneSide, MyTelegramConsts.UnPinAllMessagesDefaultPageSize));
        if (messageItems.Count == 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // Pinning an already pinned message (or unpinning a message that is not pinned) is a no-op for
        // Telegram, not a second pin: without this guard the pin flow would emit another pts bump and
        // another "pinned a message" service message. Only the caller's own copy is checked — the copy
        // of the other participant is what the pin flow is about to bring in line.
        var ownCopyInTargetState = await queryProcessor.ProcessAsync(new GetSimpleMessageListQuery(input.UserId,
            peer!, [obj.Id], !obj.Unpin, false, MyTelegramConsts.UnPinAllMessagesDefaultPageSize));
        if (ownCopyInTargetState.Count > 0)
        {
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();
        }

        // Get full message for admin log
        IMessageReadModel? fullMessage = null;
        if (peer.PeerType == PeerType.Channel)
        {
            var messagesQuery = new GetMessagesQuery(
                OwnerPeerId: peer.PeerId,
                MessageType: MessageType.Unknown,
                Q: null,
                MessageIdList: [obj.Id],
                ChannelHistoryMinId: 0,
                Limit: 1,
                Offset: null,
                Peer: peer,
                SelfUserId: input.UserId,
                Pts: 0
            );
            var messages = await queryProcessor.ProcessAsync(messagesQuery);
            fullMessage = messages.FirstOrDefault();
        }

        var command = new StartUpdatePinnedMessagesCommand(TempId.New, input.ToRequestInfo(), messageItems, peer, !obj.Unpin, pmOneSide, obj.Silent);
        await commandBus.PublishAsync(command);

        // Create admin log for channel message pin/unpin
        if (peer.PeerType == PeerType.Channel && fullMessage != null)
        {
            await CreatePinMessageAdminLogAsync(input, peer, fullMessage, !obj.Unpin);
        }

        return null !;
    }

    private async Task CreatePinMessageAdminLogAsync(IRequestInput input, Peer peer, IMessageReadModel messageItem, bool isPinned)
    {
        var messageText = messageItem.Message ?? string.Empty;
        if (string.IsNullOrEmpty(messageText) && messageItem.EncryptedData is { Length: > 0 })
        {
            messageText = messageConverterService.DecryptMessage(peer.PeerId, messageItem.MessageId, messageItem.EncryptedData.Value);
        }

        // The pinned flag is what tells the client whether the entry is a pin or an unpin.
        var message = new TMessage
        {
            Id = messageItem.MessageId,
            PeerId = new TPeerChannel { ChannelId = peer.PeerId },
            Message = messageText,
            Date = messageItem.Date,
            Out = messageItem.Out,
            Post = messageItem.Post,
            Pinned = isPinned,
            Media = new TMessageMediaEmpty(),
            ReplyTo = null,
            Entities = new TVector<IMessageEntity>()
        };

        if (messageItem.SenderUserId > 0)
        {
            message.FromId = new TPeerUser { UserId = messageItem.SenderUserId };
        }

        if (messageItem.Views is > 0)
        {
            message.Views = messageItem.Views.Value;
        }

        if (messageItem.EditDate is > 0)
        {
            message.EditDate = messageItem.EditDate.Value;
        }

        if (!string.IsNullOrEmpty(messageItem.PostAuthor))
        {
            message.PostAuthor = messageItem.PostAuthor;
        }

        await AdminLogHelper.LogUpdatePinned(mongoDatabase, peer.PeerId, input.UserId, message);
    }
}
