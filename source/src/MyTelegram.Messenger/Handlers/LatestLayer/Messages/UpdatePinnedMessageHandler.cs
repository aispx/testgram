// ReSharper disable All
using GetSimpleMessageListQuery = MyTelegram.Queries.GetSimpleMessageListQuery;
using MongoDB.Driver;
using MongoDB.Bson;

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
internal sealed class UpdatePinnedMessageHandler(ICommandBus commandBus, IPeerHelper peerHelper, IChannelAppService channelAppService, IQueryProcessor queryProcessor, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase, IMessageConverterService messageConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUpdatePinnedMessage, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestUpdatePinnedMessage obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel!.DefaultBannedRights?.PinMessages ?? true)
            {
                // Pinning is governed by pin_messages in groups and by edit_messages in broadcast
                // channels, mirroring ChatObject.canUserDoAction in the official client.
                await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId,
                    rights => channelReadModel.Broadcast ? rights.EditMessages : rights.PinMessages,
                    RpcErrors.RpcErrors400.ChatAdminRequired);
            }
        }

        var messageItems = await queryProcessor.ProcessAsync(new GetSimpleMessageListQuery(input.UserId, peer, [obj.Id], null, !obj.PmOneside, MyTelegramConsts.UnPinAllMessagesDefaultPageSize));
        if (messageItems.Count == 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
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

        var command = new StartUpdatePinnedMessagesCommand(TempId.New, input.ToRequestInfo(), messageItems, peer, !obj.Unpin, obj.PmOneside);
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
        var adminLogCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("channel_admin_log");

        IMessage message;

        if (isPinned)
        {
            // For pin: create full message with decrypted text and pinned=true
            var messageText = messageItem.Message ?? string.Empty;
            if (string.IsNullOrEmpty(messageText) && messageItem.EncryptedData.HasValue && messageItem.EncryptedData.Value.Length > 0)
            {
                messageText = messageConverterService.DecryptMessage(peer.PeerId, messageItem.MessageId, messageItem.EncryptedData.Value);
            }

            var fullMessage = new TMessage
            {
                Id = messageItem.MessageId,
                PeerId = new TPeerChannel { ChannelId = peer.PeerId },
                Message = messageText,
                Date = messageItem.Date,
                Out = messageItem.Out,
                Post = messageItem.Post,
                Pinned = true,  // CRITICAL: Set pinned=true for pin action
                Media = new TMessageMediaEmpty(),
                ReplyTo = null,
                Entities = new TVector<IMessageEntity>()
            };

            if (messageItem.SenderUserId > 0)
                fullMessage.FromId = new TPeerUser { UserId = messageItem.SenderUserId };
            if (messageItem.Views.HasValue && messageItem.Views.Value > 0)
                fullMessage.Views = messageItem.Views.Value;
            if (messageItem.EditDate.HasValue && messageItem.EditDate.Value > 0)
                fullMessage.EditDate = messageItem.EditDate.Value;
            if (!string.IsNullOrEmpty(messageItem.PostAuthor))
                fullMessage.PostAuthor = messageItem.PostAuthor;

            message = fullMessage;
        }
        else
        {
            // For unpin: create full message with Pinned=false
            var messageText = messageItem.Message ?? string.Empty;
            if (string.IsNullOrEmpty(messageText) && messageItem.EncryptedData.HasValue && messageItem.EncryptedData.Value.Length > 0)
            {
                messageText = messageConverterService.DecryptMessage(peer.PeerId, messageItem.MessageId, messageItem.EncryptedData.Value);
            }

            var fullMessage = new TMessage
            {
                Id = messageItem.MessageId,
                PeerId = new TPeerChannel { ChannelId = peer.PeerId },
                Message = messageText,
                Date = messageItem.Date,
                Out = messageItem.Out,
                Post = messageItem.Post,
                Pinned = false,  // CRITICAL: Set pinned=false for unpin action
                Media = new TMessageMediaEmpty(),
                ReplyTo = null,
                Entities = new TVector<IMessageEntity>()
            };

            if (messageItem.SenderUserId > 0)
                fullMessage.FromId = new TPeerUser { UserId = messageItem.SenderUserId };
            if (messageItem.Views.HasValue && messageItem.Views.Value > 0)
                fullMessage.Views = messageItem.Views.Value;
            if (messageItem.EditDate.HasValue && messageItem.EditDate.Value > 0)
                fullMessage.EditDate = messageItem.EditDate.Value;
            if (!string.IsNullOrEmpty(messageItem.PostAuthor))
                fullMessage.PostAuthor = messageItem.PostAuthor;

            message = fullMessage;
        }

        // Create admin log action
        var action = new TChannelAdminLogEventActionUpdatePinned { Message = message };

        // Serialize action
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        action.Serialize(buffer);
        var actionData = buffer.WrittenSpan.ToArray();

        var eventId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var logEntry = new MongoDB.Bson.BsonDocument
        {
            ["_id"] = $"adminlog-{peer.PeerId}-{eventId}",
            ["channel_id"] = peer.PeerId,
            ["event_id"] = eventId,
            ["user_id"] = input.UserId,
            ["date"] = DateTime.UtcNow,
            ["action"] = new MongoDB.Bson.BsonDocument
            {
                ["type"] = "TChannelAdminLogEventActionUpdatePinned",
                ["data"] = actionData
            }
        };

        await adminLogCol.InsertOneAsync(logEntry);
        Console.WriteLine($"[AdminLog] Created {(isPinned ? "pin" : "unpin")} message log for message {messageItem.MessageId} in channel {peer.PeerId}");
    }
}