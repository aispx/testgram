using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Scheduled;

/// <summary>
/// One message about to be put into a schedule queue.
/// </summary>
/// <param name="Item">The message itself, exactly as the normal send path built it.</param>
/// <param name="RepeatPeriod"><c>schedule_repeat_period</c>, when the message repeats.</param>
/// <param name="PreallocatedMessageId">Real message id reserved by the caller (paid media).</param>
/// <param name="VideoProcessingPending">The message waits for its video to be converted.</param>
public sealed record ScheduledQueueItem(MessageItem Item, int? RepeatPeriod, int? PreallocatedMessageId,
    bool VideoProcessingPending = false);

/// <summary>
/// Storage and rendering of the schedule queues.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public interface IScheduledMessageStore
{
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the user may look at the schedule queue of <paramref name="peer"/> and tells whether
    /// that queue is shared. Broadcast channels have one queue for all admins; everywhere else each user
    /// only ever sees the messages they queued themselves.
    /// </summary>
    Task<bool> CheckQueueAccessAsync(Peer peer, long selfUserId);

    /// <summary>
    /// User ids that must be told about a change to the schedule queue of <paramref name="peer"/>.
    /// A broadcast channel shares one queue, so every admin allowed to post is notified (the clients
    /// key scheduled messages by peer, so the update simply drops into each admin's scheduled view);
    /// everywhere else only <paramref name="senderUserId"/> is.
    /// </summary>
    Task<IReadOnlyList<long>> GetQueueAudienceAsync(Peer peer, long senderUserId);

    Task<List<ScheduledMessageDocument>> GetQueueAsync(Peer peer, long selfUserId, bool sharedQueue,
        IReadOnlyList<int>? scheduledMessageIds = null);

    Task<long> CountAsync(Peer peer, long senderUserId);

    Task<List<ScheduledMessageDocument>> SaveAsync(IReadOnlyList<ScheduledQueueItem> items, RequestInfo requestInfo);

    Task ReplaceAsync(ScheduledMessageDocument document);

    Task DeleteAsync(IEnumerable<string> documentIds);

    /// <summary>
    /// Entries whose time has come, already claimed for <paramref name="leaseSeconds"/> by this caller.
    /// </summary>
    Task<List<ScheduledMessageDocument>> ClaimDueAsync(int now, int limit, int leaseSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Entries whose video is still to be converted, claimed the same way as <see cref="ClaimDueAsync"/>.
    /// </summary>
    Task<List<ScheduledMessageDocument>> ClaimVideoProcessingAsync(int limit, int leaseSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Entries waiting for their peer to come online, claimed the same way as <see cref="ClaimDueAsync"/>.
    /// </summary>
    Task<List<ScheduledMessageDocument>> ClaimWhenOnlineAsync(IReadOnlyCollection<long> onlineUserIds, int limit,
        int leaseSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unix timestamp of the earliest entry still waiting for a fixed date, or null when there is none.
    /// </summary>
    Task<int?> GetNextScheduleDateAsync(CancellationToken cancellationToken = default);

    Task ReleaseAsync(ScheduledMessageDocument document, int nextAttemptDate);

    IMessage Render(ScheduledMessageDocument document, long selfUserId, int layer);

    TUpdates BuildNewScheduledUpdates(IReadOnlyList<ScheduledMessageDocument> documents, long selfUserId, int layer);

    TUpdates BuildDeleteScheduledUpdates(Peer peer, IReadOnlyList<int> scheduledMessageIds,
        IReadOnlyList<int>? sentMessageIds = null);

    SendMessageInput BuildSendInput(ScheduledMessageDocument document, RequestInfo requestInfo, int messageId,
        int groupItemCount = 1);

    /// <summary>
    /// Peer and account dependent validation of a scheduling request (dates, limits, premium, privacy).
    /// </summary>
    Task ValidateAsync(long senderUserId, Peer toPeer, int scheduleDate, int? repeatPeriod, int batchSize);
}
