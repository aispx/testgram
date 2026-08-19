using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// One entry of a schedule queue, stored in the MongoDB collection <c>scheduled_messages</c>.
/// Each PM, chat, supergroup and channel has its own queue.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
/// <remarks>
/// The whole <see cref="MessageItem"/> is persisted so the queued message keeps its media, entities,
/// reply header, keyboard, album id and so on: rendering the queue and flushing it both go through the
/// same object the normal send path uses. The message text is stored in clear even when message
/// encryption is enabled — encryption is applied again when the message finally leaves the queue.
/// </remarks>
public class ScheduledMessageDocument
{
    /// <summary>
    /// <c>scheduled-{OwnerPeerId}-{ScheduledMessageId}</c>.
    /// </summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Id of the message inside the schedule queue of the peer. Allocated from the same per peer
    /// sequence as real message ids, so a queue id can never collide with a sent message id.
    /// </summary>
    public int ScheduledMessageId { get; set; }

    /// <summary>
    /// Channel id for channels, sender user id otherwise (same rule as <c>MessageItem.OwnerPeer</c>).
    /// </summary>
    public long OwnerPeerId { get; set; }

    public long SenderUserId { get; set; }

    public long PeerId { get; set; }

    public string PeerType { get; set; } = string.Empty;

    /// <summary>
    /// Unix timestamp the message must be sent at, or <see cref="ScheduledMessageRules.WhenOnlineDate"/>
    /// for "send when the peer comes online".
    /// </summary>
    public int ScheduleDate { get; set; }

    /// <summary>
    /// <c>schedule_repeat_period</c>: after being sent the message is re-scheduled this many seconds
    /// in the future. Premium only.
    /// </summary>
    public int? RepeatPeriod { get; set; }

    /// <summary>
    /// Real message id reserved before the message was queued (paid media binds its items to it).
    /// When set it is reused as the id of the sent message instead of allocating a new one.
    /// </summary>
    public int? PreallocatedMessageId { get; set; }

    public MessageItem Item { get; set; } = default!;

    public int? EditDate { get; set; }

    /// <summary>
    /// Layer of the client that queued the message, used when the queue is rendered for a push.
    /// </summary>
    public int Layer { get; set; }

    public long RandomId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set while the video of this message is still being converted server side. Such an entry waits
    /// for the converter, not for the clock, and its <see cref="ScheduleDate"/> is the estimated
    /// conversion date shown by the client.
    /// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
    /// </summary>
    public bool VideoProcessingPending { get; set; }

    /// <summary>
    /// Lease taken by the background sender before it flushes the entry, so two command servers
    /// never send the same queued message twice.
    /// </summary>
    public DateTime? ClaimedUntil { get; set; }

    /// <summary>
    /// Failed flush attempts; drives the retry backoff in <see cref="ScheduledMessageSender"/>.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Unix timestamp before which a failed entry must not be retried.
    /// </summary>
    public int? NextAttemptDate { get; set; }
}
