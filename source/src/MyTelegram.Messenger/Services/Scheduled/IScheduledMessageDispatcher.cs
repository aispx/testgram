namespace MyTelegram.Messenger.Services.Scheduled;

/// <summary>
/// Flushes entries out of a schedule queue: sends the messages, drops (or re-schedules) the entries and
/// emits <c>updateDeleteScheduledMessages</c>.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public interface IScheduledMessageDispatcher
{
    /// <summary>
    /// Sends the queued messages right away.
    /// </summary>
    /// <param name="documents">Entries to flush. May span several peers.</param>
    /// <param name="requestInfo">
    /// Request that triggered the flush (messages.sendScheduledMessages), or null when the background
    /// sender fires the queue on time.
    /// </param>
    /// <returns>
    /// The <c>updateDeleteScheduledMessages</c> updates for every affected peer, with the sent message
    /// ids at the same vector indexes as the scheduled ones.
    /// </returns>
    Task<IUpdates> FlushAsync(IReadOnlyList<ScheduledMessageDocument> documents, RequestInfo? requestInfo = null);
}
