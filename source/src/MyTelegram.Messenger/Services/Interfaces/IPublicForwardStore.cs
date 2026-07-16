using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// The Public_Forward_Store: records and removes public forwards of messages and stories and returns
/// stably-ordered pages and the total non-removed count for a source.
/// </summary>
public interface IPublicForwardStore
{
    /// <summary>
    /// Records a public forward for <paramref name="source"/>, deduped on the forwarding message.
    /// Only forwards from peers with a public username should be recorded by callers.
    /// </summary>
    Task RecordAsync(ForwardSourceKey source, PublicForwardRecord record);

    /// <summary>
    /// Soft-removes the recorded forward identified by <paramref name="forwardRef"/> for <paramref name="source"/>.
    /// </summary>
    Task RemoveAsync(ForwardSourceKey source, ForwardRefKey forwardRef);

    /// <summary>
    /// Returns the number of currently-recorded, non-removed forwards for <paramref name="source"/>.
    /// </summary>
    Task<int> CountAsync(ForwardSourceKey source);

    /// <summary>
    /// Returns a stably-ordered page of forwards for <paramref name="source"/> using <paramref name="offset"/>
    /// as an opaque cursor and clamping <paramref name="limit"/> to <c>1..100</c>.
    /// </summary>
    Task<PublicForwardPage> GetPageAsync(ForwardSourceKey source, string offset, int limit);
}
