using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Builds a <see cref="PushData"/> (loc_key + loc_args + custom) for an incoming message, using the
/// same loc-key taxonomy official clients localise in <c>PushListenerController</c>
/// (<see href="https://corefork.telegram.org/api/push-updates">PUSH updates</see>).
/// <para>
/// The builder resolves the sender's display name on the fly (first name, or last name when only
/// that is set) so the client can substitute <c>{1}</c> in the localised string.
/// </para>
/// </summary>
public class MessagePushDataBuilder(IUserAppService userAppService) : ITransientDependency
{
    /// <summary>
    /// Builds a push for a 1:1 (user-to-user) message. Returns null when no push should be shown
    /// (e.g. service notifications that aren't user-visible).
    /// </summary>
    public async Task<PushData?> BuildForPersonalMessageAsync(MessageItem item)
    {
        if (item is { SendMessageType: SendMessageType.MessageService } or
            { MessageActionType: not MessageActionType.None })
        {
            // Service actions (typing/created-chat/etc.) are not pushed for PMs in this pass.
            return null;
        }

        var senderName = await GetDisplayNameAsync(item.SenderUserId);
        var locKey = ResolvePersonalLocKey(item);
        var locArgs = BuildArgs(locKey, senderName, item.Message);
        var isMention = item.MentionedUserIds?.Count > 0;

        var custom = new PushNotificationCustomData
        {
            FromId = item.SenderUserId,
            MsgId = item.MessageId,
            RandomId = item.RandomId,
            Silent = item.Silent,
            Mention = isMention,
            Schedule = item.ScheduleDate.HasValue,
            EditDate = item.EditDate,
            TopMsgId = item.TopMsgId,
            Attachb64 = ResolveAttachb64(item.Media)
        };

        if (item.ReportDeliveryUntilDate is { } personalReportUntil and not 0)
        {
            custom.ReportDeliveryUntilDate = personalReportUntil;
        }

        return new PushData(
            LocKey: locKey,
            LocArgs: locArgs,
            UserId: item.OwnerPeer.PeerId,
            Custom: custom,
            Sound: item.Silent ? null : "default");
    }

    /// <summary>
    /// Builds a channel/group push. <paramref name="chatName"/> is the channel/group title used as
    /// the first localisation argument for CHANNEL_*/CHAT_* keys.
    /// </summary>
    public async Task<PushData?> BuildForChannelMessageAsync(MessageItem item, string chatName)
    {
        var senderName = await GetDisplayNameAsync(item.SenderUserId);
        var locKey = ResolveChannelLocKey(item);
        var locArgs = BuildArgs(locKey, chatName, item.Message);
        var isMention = item.MentionedUserIds?.Count > 0;

        var custom = new PushNotificationCustomData
        {
            ChannelId = item.ToPeer.PeerType == PeerType.Channel ? item.ToPeer.PeerId : 0,
            MsgId = item.MessageId,
            RandomId = item.RandomId,
            Silent = item.Silent,
            Mention = isMention,
            Schedule = item.ScheduleDate.HasValue,
            Attachb64 = ResolveAttachb64(item.Media)
        };

        if (item.ReportDeliveryUntilDate is { } channelReportUntil and not 0)
        {
            custom.ReportDeliveryUntilDate = channelReportUntil;
        }

        return new PushData(
            LocKey: locKey,
            LocArgs: locArgs,
            UserId: 0, // resolved per-target in the dispatcher via OnlySendToUserId
            Custom: custom,
            Sound: item.Silent ? null : "default");
    }

    /// <summary>
    /// Builds a service (cancel) push telling the client that messages were deleted, so it can remove
    /// the corresponding notifications (<c>MESSAGE_DELETED</c>). <c>custom.messages</c> carries the
    /// comma-separated list of deleted message IDs.
    /// </summary>
    public PushData BuildMessageDeleted(long recipientUserId, Peer peer, IReadOnlyList<int> messageIds)
    {
        var custom = new PushNotificationCustomData
        {
            Messages = string.Join(",", messageIds)
        };
        ApplyPeerToCustom(custom, peer);

        return new PushData(
            LocKey: PushNotificationTypes.MessageDeleted,
            LocArgs: [],
            UserId: recipientUserId,
            Custom: custom,
            Sound: null);
    }

    /// <summary>
    /// Builds a service (cancel) push telling the client that the chat history was read up to
    /// <paramref name="maxId"/> (<c>READ_HISTORY</c>), so it can remove stale notifications for the chat.
    /// </summary>
    public PushData BuildReadHistory(long recipientUserId, Peer peer, int maxId)
    {
        var custom = new PushNotificationCustomData
        {
            MaxId = maxId
        };
        ApplyPeerToCustom(custom, peer);

        return new PushData(
            LocKey: PushNotificationTypes.ReadHistory,
            LocArgs: [],
            UserId: recipientUserId,
            Custom: custom,
            Sound: null);
    }

    /// <summary>
    /// Builds a service (cancel) push telling the client that reactions were read on the given messages
    /// (<c>READ_REACTION</c>). <c>custom.messages</c> carries the comma-separated list of message IDs.
    /// </summary>
    public PushData BuildReadReaction(long recipientUserId, Peer peer, IReadOnlyList<int> messageIds)
    {
        var custom = new PushNotificationCustomData
        {
            Messages = string.Join(",", messageIds)
        };
        ApplyPeerToCustom(custom, peer);

        return new PushData(
            LocKey: PushNotificationTypes.ReadReaction,
            LocArgs: [],
            UserId: recipientUserId,
            Custom: custom,
            Sound: null);
    }

    /// <summary>
    /// Builds a push telling the recipient that <paramref name="reactorName"/> reacted with
    /// <paramref name="reaction"/> to one of their messages. The <c>loc_key</c> is drawn from the
    /// <c>REACT_*</c> family for 1:1 chats and the <c>CHAT_REACT_*</c> family for groups/channels,
    /// matching the official client taxonomy. <c>custom.msg_id</c> carries the id of the message the
    /// reaction was placed on, and the chat-identifier (<c>from_id</c>/<c>chat_id</c>/<c>channel_id</c>)
    /// is set according to the peer type.
    /// </summary>
    public PushData BuildReaction(long recipientUserId, MessageItem reactedMessage, string reactorName,
        string reaction, string? chatName = null)
    {
        var isGroup = reactedMessage.ToPeer.PeerType is PeerType.Chat or PeerType.Channel;
        var locKey = ResolveReactionLocKey(reactedMessage, isGroup);

        var custom = new PushNotificationCustomData
        {
            MsgId = reactedMessage.MessageId,
            Silent = reactedMessage.Silent
        };
        ApplyPeerToCustom(custom, reactedMessage.ToPeer);

        // PM react keys take {name, reaction}; group react keys take {name, chatName, reaction}.
        var locArgs = isGroup
            ? new[] { reactorName, chatName ?? string.Empty, reaction }
            : new[] { reactorName, reaction };

        return new PushData(
            LocKey: locKey,
            LocArgs: locArgs,
            UserId: recipientUserId,
            Custom: custom,
            Sound: reactedMessage.Silent ? null : "default");
    }

    /// <summary>
    /// Resolves the reaction <c>loc_key</c> from the <c>REACT_*</c> (1:1) or <c>CHAT_REACT_*</c>
    /// (group/channel) family based on the reacted message's media/text content.
    /// </summary>
    private static string ResolveReactionLocKey(MessageItem item, bool isGroup)
    {
        if (item.Media is null)
        {
            if (string.IsNullOrWhiteSpace(item.Message))
            {
                return isGroup ? PushNotificationTypes.ChatReactNotext : PushNotificationTypes.ReactNotext;
            }
            return isGroup ? PushNotificationTypes.ChatReactText : PushNotificationTypes.ReactText;
        }

        return item.MessageType switch
        {
            MessageType.Photo => isGroup ? PushNotificationTypes.ChatReactPhoto : PushNotificationTypes.ReactPhoto,
            MessageType.Video => isGroup ? PushNotificationTypes.ChatReactVideo : PushNotificationTypes.ReactVideo,
            MessageType.Voice => isGroup ? PushNotificationTypes.ChatReactAudio : PushNotificationTypes.ReactAudio,
            MessageType.Gif => isGroup ? PushNotificationTypes.ChatReactGif : PushNotificationTypes.ReactGif,
            MessageType.Geo => isGroup ? PushNotificationTypes.ChatReactGeo : PushNotificationTypes.ReactGeo,
            MessageType.Game => isGroup ? PushNotificationTypes.ChatReactGame : PushNotificationTypes.ReactGame,
            MessageType.Poll => isGroup ? PushNotificationTypes.ChatReactPoll : PushNotificationTypes.ReactPoll,
            MessageType.Contacts => isGroup ? PushNotificationTypes.ChatReactContact : PushNotificationTypes.ReactContact,
            MessageType.Invoice => isGroup ? PushNotificationTypes.ChatReactInvoice : PushNotificationTypes.ReactInvoice,
            MessageType.Document => isGroup ? PushNotificationTypes.ChatReactDoc : PushNotificationTypes.ReactDoc,
            _ => isGroup ? PushNotificationTypes.ChatReactDoc : PushNotificationTypes.ReactDoc
        };
    }

    /// <summary>
    /// Builds an incoming-call push (<c>PHONE_CALL_REQUEST</c>). <c>custom.call_id</c> and
    /// <c>custom.call_ah</c> carry the call identifier and access hash, and <c>custom.updates</c>
    /// carries the base64url TL-serialization of the <c>Updates</c> object (containing
    /// <c>updatePhoneCall</c>) so the client can ring without an active session.
    /// </summary>
    public PushData BuildPhoneCall(long recipientUserId, long callId, long callAh, byte[] updatesTl)
    {
        var custom = new PushNotificationCustomData
        {
            CallId = callId,
            CallAh = callAh,
            Updates = updatesTl.ToBase64Url()
        };

        return new PushData(
            LocKey: PushNotificationTypes.PhoneCallRequest,
            LocArgs: [],
            UserId: recipientUserId,
            Custom: custom,
            Sound: null);
    }

    /// <summary>
    /// Populates the chat-identifier on <paramref name="custom"/> matching the official taxonomy:
    /// <c>channel_id</c> for channels/supergroups, <c>chat_id</c> for basic groups and <c>from_id</c> for PMs.
    /// </summary>
    private static void ApplyPeerToCustom(PushNotificationCustomData custom, Peer peer)
    {
        switch (peer.PeerType)
        {
            case PeerType.Channel:
                custom.ChannelId = peer.PeerId;
                break;
            case PeerType.Chat:
                custom.ChatId = peer.PeerId;
                break;
            default:
                custom.FromId = peer.PeerId;
                break;
        }
    }

    /// <summary>
    /// Builds the base64url TL-serialization of the media attachment (<c>custom.attachb64</c>) for a
    /// media message, per Requirement 4.8. Returns the base64url-encoded TL bytes of the underlying
    /// <c>Photo</c> (for <see cref="TMessageMediaPhoto"/>) or <c>Document</c> (for
    /// <see cref="TMessageMediaDocument"/>), or <c>null</c> when the message has no media attachment
    /// (or the media carries no Photo/Document object).
    /// </summary>
    private static string? ResolveAttachb64(IMessageMedia? media)
    {
        return media switch
        {
            TMessageMediaPhoto { Photo: { } photo } => photo.ToBytes().ToBase64Url(),
            TMessageMediaDocument { Document: { } document } => document.ToBytes().ToBase64Url(),
            _ => null
        };
    }

    private async Task<string> GetDisplayNameAsync(long userId)
    {
        try
        {
            var user = await userAppService.GetAsync(userId);
            if (user == null)
            {
                return "Unknown";
            }
            if (!string.IsNullOrWhiteSpace(user.FirstName))
            {
                return string.IsNullOrWhiteSpace(user.LastName)
                    ? user.FirstName
                    : $"{user.FirstName} {user.LastName}";
            }
            return !string.IsNullOrWhiteSpace(user.LastName) ? user.LastName :
                   !string.IsNullOrWhiteSpace(user.UserName) ? user.UserName : "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string ResolvePersonalLocKey(MessageItem item)
    {
        // No media => text message. Otherwise map by media/document attribute type.
        if (item.Media is null)
        {
            return string.IsNullOrWhiteSpace(item.Message) ? PushNotificationTypes.MessageNotext : PushNotificationTypes.MessageText;
        }

        return item.MessageType switch
        {
            MessageType.Photo => PushNotificationTypes.MessagePhoto,
            MessageType.Video => PushNotificationTypes.MessageVideo,
            MessageType.Voice => PushNotificationTypes.MessageAudio,
            MessageType.Music => PushNotificationTypes.MessagePlaylist,
            MessageType.Gif => PushNotificationTypes.MessageGif,
            MessageType.Geo => PushNotificationTypes.MessageGeo,
            MessageType.Game => PushNotificationTypes.MessageGame,
            MessageType.Poll => PushNotificationTypes.MessagePoll,
            MessageType.Contacts => PushNotificationTypes.MessageContact,
            MessageType.Invoice => PushNotificationTypes.MessageInvoice,
            MessageType.Document => PushNotificationTypes.MessageDoc,
            _ => PushNotificationTypes.MessageDoc
        };
    }

    private static string ResolveChannelLocKey(MessageItem item)
    {
        if (item.Media is null)
        {
            return string.IsNullOrWhiteSpace(item.Message) ? PushNotificationTypes.ChannelMessageNotext : PushNotificationTypes.ChannelMessageText;
        }
        return item.MessageType switch
        {
            MessageType.Photo => PushNotificationTypes.ChannelMessagePhoto,
            MessageType.Video => PushNotificationTypes.ChannelMessageVideo,
            MessageType.Voice => PushNotificationTypes.ChannelMessageAudio,
            MessageType.Document => PushNotificationTypes.ChannelMessageDoc,
            MessageType.Gif => PushNotificationTypes.ChannelMessageGif,
            MessageType.Geo => PushNotificationTypes.ChannelMessageGeo,
            MessageType.Game => PushNotificationTypes.ChannelMessageGame,
            MessageType.Poll => PushNotificationTypes.ChannelMessagePoll,
            _ => PushNotificationTypes.ChannelMessageNotext
        };
    }

    /// <summary>
    /// Assembles loc_args. Most keys take {sender, body}; media-without-text keys take just
    /// {sender}; counters (e.g. albums) would take more. We keep it conservative here.
    /// </summary>
    private static string[] BuildArgs(string locKey, string name, string body)
    {
        // Text keys carry two args: {name, body}. Everything else (media) takes only {name}.
        if (locKey == PushNotificationTypes.MessageText
            || locKey == PushNotificationTypes.ChannelMessageText
            || locKey == PushNotificationTypes.ChatMessageText)
        {
            return [name, body];
        }
        return [name];
    }
}
