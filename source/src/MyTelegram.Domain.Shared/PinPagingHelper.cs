namespace MyTelegram;

/// <summary>
/// Paging rules of messages.unpinAllMessages, which unpins at most one page per call and reports the
/// progress through <c>messages.affectedHistory.offset</c>: a non-zero offset means the client has to
/// call the method again, zero means everything has been unpinned.
/// See https://corefork.telegram.org/method/messages.unpinAllMessages
/// </summary>
public static class PinPagingHelper
{
    /// <summary>
    /// True when the fetched page is not full, i.e. no pinned message is left behind for a next call.
    /// </summary>
    public static bool IsLastBatch(int fetchedCount)
    {
        return fetchedCount < MyTelegramConsts.UnPinAllMessagesDefaultPageSize;
    }

    /// <summary>
    /// The offset to report back: 0 once the last page has been processed, otherwise the highest
    /// message id of the page so the client can resume from there.
    /// </summary>
    public static int CalculateOffset(bool lastBatch, IEnumerable<int> messageIds)
    {
        return lastBatch ? 0 : messageIds.DefaultIfEmpty(0).Max();
    }
}
