using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Transfer channel ownership
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_ADMIN_PUBLIC_TOO_MUCH You're admin of too many public channels, make some channels private to change the username of this channel.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_MEMBER_ADD_FAILED Could not add participants.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 PASSWORD_HASH_INVALID The provided password hash is invalid.
/// 400 PASSWORD_MISSING You must <a href="https://corefork.telegram.org/api/srp">enable 2FA</a> before executing this operation.
/// 400 PASSWORD_TOO_FRESH_%d The password was modified less than 24 hours ago, try again in %d seconds.
/// 400 SESSION_TOO_FRESH_%d This session was created less than 24 hours ago, try again in %d seconds.
/// 400 SRP_ID_INVALID Invalid SRP ID provided.
/// 400 USERS_TOO_MUCH The maximum number of users has been exceeded (to create a chat, for example).
/// 403 USER_CHANNELS_TOO_MUCH One of the users you tried to add is already in too many channels/supergroups.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 400 USER_NOT_MUTUAL_CONTACT The provided user is not a mutual contact.
/// 403 USER_PRIVACY_RESTRICTED The user's privacy settings do not allow you to do this.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.editCreator"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditCreatorHandler(
    IMongoDatabase database,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestEditCreator, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestEditCreator obj)
    {
        // Get channel
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var channelId = inputChannel.ChannelId;

        // Get channel from DB
        var channelCol = database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channel = await channelCol.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channel == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        // Check if current user is creator
        var creatorId = channel!.Contains("CreatorId") ? channel["CreatorId"].AsInt64 : 0;
        if (creatorId != input.UserId)
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();

        // Get new owner user ID
        var userInput = obj.UserId as TInputUser;
        if (userInput == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        var newOwnerId = userInput!.UserId;
        if (newOwnerId == 0)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        // Check if new owner is same as current
        if (newOwnerId == input.UserId)
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();

        // Verify new owner exists
        var userCol = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var newOwner = await userCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", newOwnerId)).FirstOrDefaultAsync();
        if (newOwner == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        // Check if new owner is member of channel
        var memberCol = database.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
        var newOwnerMember = await memberCol.Find(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                Builders<BsonDocument>.Filter.Eq("UserId", newOwnerId),
                Builders<BsonDocument>.Filter.Eq("Left", false)
            )
        ).FirstOrDefaultAsync();

        if (newOwnerMember == null)
            RpcErrors.RpcErrors400.UserNotParticipant.ThrowRpcError();

        // Create pending transfer record
        var transfersCol = database.GetCollection<BsonDocument>("channel_pending_transfers");
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(48);

        var transferDoc = new BsonDocument
        {
            ["_id"] = $"transfer-{channelId}-{now:yyyyMMddHHmmss}",
            ["ChannelId"] = channelId,
            ["FromUserId"] = input.UserId,
            ["ToUserId"] = newOwnerId,
            ["CreatedAt"] = now,
            ["ExpiresAt"] = expiresAt
        };

        await transfersCol.InsertOneAsync(transferDoc);

        // Log ownership transfer in admin log
        var prevParticipant = new MyTelegram.Schema.TChannelParticipantCreator
        {
            UserId = input.UserId,
            AdminRights = new TChatAdminRights { Flags = int.MaxValue },
            Rank = "Owner"
        };

        var newParticipant = new MyTelegram.Schema.TChannelParticipantCreator
        {
            UserId = newOwnerId,
            AdminRights = new TChatAdminRights { Flags = int.MaxValue },
            Rank = "Owner"
        };

        await AdminLogHelper.LogEditAdmin(database, channelId, input.UserId, prevParticipant, newParticipant);

        // Get channel title and new owner name
        var channelTitle = channel["Title"].AsString;
        var newOwnerName = newOwner!.Contains("FirstName") ? newOwner["FirstName"].AsString : "User";
        if (newOwner.Contains("LastName") && !newOwner["LastName"].IsBsonNull)
        {
            newOwnerName += " " + newOwner["LastName"].AsString;
        }

        // Send notification to new owner from Telegram service bot (777000)
        var messageText = $"⚠️ Channel Transferred to You: {channelTitle}\n\n" +
                         $"The previous owner has transferred ownership of this channel to you. You are now the channel owner.\n\n" +
                         $"If this was a mistake, you can reject the transfer by pressing 'Reject Channel Transfer' below. " +
                         $"Otherwise, the transfer will be automatically accepted in 48 hours.";

        // Create inline keyboard with reject button
        var rejectButton = new TKeyboardButtonCallback
        {
            Text = "Reject Channel Transfer",
            Data = $"reject_channel_transfer:{channelId}:{input.UserId}"
        };

        var keyboard = new TReplyInlineMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>
            {
                new TKeyboardButtonRow
                {
                    Buttons = new TVector<IKeyboardButton> { rejectButton }
                }
            }
        };

        var sendInput = new SendMessageInput(
            new RequestInfo(
                ConnectionId: string.Empty,
                SessionId: 0,
                ReqMsgId: 0,
                UserId: 777000, // Telegram service bot
                AccessHashKeyId: 0,
                AuthKeyId: 0,
                PermAuthKeyId: 0,
                RequestId: Guid.NewGuid(),
                Layer: 222,
                Date: DateTime.UtcNow.ToTimestamp(),
                DeviceType: DeviceType.Android
            ),
            777000,
            new Peer(PeerType.User, newOwnerId),
            messageText,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Text,
            messageType: MessageType.Text,
            replyMarkup: keyboard
        );

        await messageAppService.SendMessageAsync([sendInput]);

        return new TUpdates
        {
            Users = new TVector<IUser>(),
            Updates = new TVector<IUpdate>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate
        };
    }
}