using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Helpers;

public static class AdminLogHelper
{
    public static async Task LogChangeTitle(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeTitle
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeAbout(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeAbout
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeUsername(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeUsername
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's message accent
    /// <a href="https://core.telegram.org/api/colors">peer color</a>.
    /// </summary>
    public static async Task LogChangePeerColor(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPeerColor? prevValue,
        IPeerColor? newValue)
    {
        // PrevValue/NewValue are non-nullable in the schema and always serialized,
        // so an unset color is represented by an empty TPeerColor.
        var action = new TChannelAdminLogEventActionChangePeerColor
        {
            PrevValue = prevValue ?? new TPeerColor(),
            NewValue = newValue ?? new TPeerColor()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's profile page
    /// <a href="https://core.telegram.org/api/colors">peer color</a>.
    /// </summary>
    public static async Task LogChangeProfilePeerColor(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPeerColor? prevValue,
        IPeerColor? newValue)
    {
        var action = new TChannelAdminLogEventActionChangeProfilePeerColor
        {
            PrevValue = prevValue ?? new TPeerColor(),
            NewValue = newValue ?? new TPeerColor()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's
    /// <a href="https://core.telegram.org/api/emoji-status">emoji status</a>.
    /// </summary>
    public static async Task LogChangeEmojiStatus(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IEmojiStatus? prevValue,
        IEmojiStatus? newValue)
    {
        // PrevValue/NewValue are non-nullable in the schema and always serialized,
        // so an unset status is represented by emojiStatusEmpty.
        var action = new TChannelAdminLogEventActionChangeEmojiStatus
        {
            PrevValue = prevValue ?? new TEmojiStatusEmpty(),
            NewValue = newValue ?? new TEmojiStatusEmpty()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleInvites(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleInvites
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleSignatures(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleSignatures
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantJoin(
        IMongoDatabase database,
        long channelId,
        long userId)
    {
        var action = new TChannelAdminLogEventActionParticipantJoin();
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogParticipantLeave(
        IMongoDatabase database,
        long channelId,
        long userId)
    {
        var action = new TChannelAdminLogEventActionParticipantLeave();
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogDeleteMessage(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionDeleteMessage
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditMessage(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage prevMessage,
        IMessage newMessage)
    {
        var action = new TChannelAdminLogEventActionEditMessage
        {
            PrevMessage = prevMessage,
            NewMessage = newMessage
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogTogglePreHistoryHidden(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionTogglePreHistoryHidden
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditAdmin(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantToggleAdmin
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditBanned(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeStickerSet(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IInputStickerSet prevStickerset,
        IInputStickerSet newStickerset)
    {
        var action = new TChannelAdminLogEventActionChangeStickerSet
        {
            PrevStickerset = prevStickerset,
            NewStickerset = newStickerset
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangePhoto(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPhoto prevPhoto,
        IPhoto newPhoto)
    {
        var action = new TChannelAdminLogEventActionChangePhoto
        {
            PrevPhoto = prevPhoto,
            NewPhoto = newPhoto
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeHistoryTTL(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        int prevValue,
        int newValue)
    {
        var action = new TChannelAdminLogEventActionChangeHistoryTTL
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantJoinByInvite(
        IMongoDatabase database,
        long channelId,
        long userId,
        bool viaChatlist,
        MyTelegram.Schema.IExportedChatInvite invite)
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByInvite
        {
            ViaChatlist = viaChatlist,
            Invite = invite
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    /// <summary>
    /// Logs that an admin let a <a href="https://corefork.telegram.org/api/invites#join-requests">join
    /// request</a> through. The event belongs to the user who joined, the approving admin is carried
    /// by <c>approved_by</c>.
    /// </summary>
    public static async Task LogParticipantJoinByRequest(
        IMongoDatabase database,
        long channelId,
        long userId,
        MyTelegram.Schema.IExportedChatInvite invite,
        long approvedBy)
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByRequest
        {
            Invite = invite,
            ApprovedBy = approvedBy
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogToggleSlowMode(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        int prevValue,
        int newValue)
    {
        var action = new TChannelAdminLogEventActionToggleSlowMode
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleForum(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleForum
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogUpdatePinned(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionUpdatePinned
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeLinkedChat(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        long prevValue,
        long newValue)
    {
        var action = new TChannelAdminLogEventActionChangeLinkedChat
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeLocation(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChannelLocation prevValue,
        IChannelLocation newValue)
    {
        var action = new TChannelAdminLogEventActionChangeLocation
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogDefaultBannedRights(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChatBannedRights prevBannedRights,
        IChatBannedRights newBannedRights)
    {
        var action = new TChannelAdminLogEventActionDefaultBannedRights
        {
            PrevBannedRights = prevBannedRights,
            NewBannedRights = newBannedRights
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantSubExtend(
        IMongoDatabase database,
        long channelId,
        long userId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantSubExtend
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogCreateTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic topic)
    {
        var action = new TChannelAdminLogEventActionCreateTopic
        {
            Topic = topic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic prevTopic,
        IForumTopic newTopic)
    {
        var action = new TChannelAdminLogEventActionEditTopic
        {
            PrevTopic = prevTopic,
            NewTopic = newTopic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogDeleteTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic topic)
    {
        var action = new TChannelAdminLogEventActionDeleteTopic
        {
            Topic = topic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogPinTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic prevTopic,
        IForumTopic newTopic)
    {
        var action = new TChannelAdminLogEventActionPinTopic
        {
            PrevTopic = prevTopic,
            NewTopic = newTopic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogStartGroupCall(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionStartGroupCall
        {
            Call = GroupCallStateHelper.ToInputGroupCall(call)
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    public static async Task LogDiscardGroupCall(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionDiscardGroupCall
        {
            Call = GroupCallStateHelper.ToInputGroupCall(call)
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    public static async Task LogToggleGroupCallSetting(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId,
        bool joinMuted)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionToggleGroupCallSetting
        {
            JoinMuted = joinMuted
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    private static async Task LogEventAsync(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChannelAdminLogEventAction action)
    {
        var collection = database.GetCollection<BsonDocument>("channel_admin_log");
        var eventId = Random.Shared.NextInt64();

        var buffer = new ArrayBufferWriter<byte>();
        action.Serialize(buffer);

        await collection.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"adminlog-{channelId}-{eventId}",
            ["event_id"] = eventId,
            ["channel_id"] = channelId,
            ["user_id"] = adminUserId,
            ["date"] = DateTime.UtcNow,
            ["action"] = new BsonDocument
            {
                ["type"] = action.GetType().Name,
                ["data"] = buffer.WrittenMemory.ToArray()
            }
        });
    }
}
