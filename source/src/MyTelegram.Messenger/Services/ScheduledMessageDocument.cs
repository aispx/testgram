using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Stored in MongoDB collection "scheduled_messages"
/// </summary>
public class ScheduledMessageDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// Scheduled message ID (unique per peer)
    /// </summary>
    public int ScheduledMessageId { get; set; }

    /// <summary>
    /// Sender user ID
    /// </summary>
    public long SenderUserId { get; set; }

    /// <summary>
    /// Target peer ID (user/chat/channel)
    /// </summary>
    public long PeerId { get; set; }

    /// <summary>
    /// Peer type (User/Chat/Channel)
    /// </summary>
    public string PeerType { get; set; } = "";

    /// <summary>
    /// Message text
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Schedule date (unix timestamp)
    /// </summary>
    public int ScheduleDate { get; set; }

    /// <summary>
    /// Random ID for deduplication
    /// </summary>
    public long RandomId { get; set; }

    /// <summary>
    /// Reply to message ID
    /// </summary>
    public int? ReplyToMsgId { get; set; }

    /// <summary>
    /// Message entities (bold, italic, etc)
    /// </summary>
    public string? Entities { get; set; }

    /// <summary>
    /// Media type (photo, document, etc)
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>
    /// Serialized media data
    /// </summary>
    public string? MediaData { get; set; }

    /// <summary>
    /// Reply markup (keyboard)
    /// </summary>
    public string? ReplyMarkup { get; set; }

    /// <summary>
    /// Silent message flag
    /// </summary>
    public bool Silent { get; set; }

    /// <summary>
    /// No forwards flag
    /// </summary>
    public bool NoForwards { get; set; }

    /// <summary>
    /// Created at timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
