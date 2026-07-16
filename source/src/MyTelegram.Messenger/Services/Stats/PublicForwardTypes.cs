namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// The kind of source a public forward references.
/// </summary>
public enum ForwardSourceType
{
    Message,
    Story
}

/// <summary>
/// Identifies the source message or story whose public forwards are tracked.
/// <para><c>ItemId</c> is the source message id or story id.</para>
/// </summary>
public readonly record struct ForwardSourceKey(ForwardSourceType Type, long OwnerPeerId, long ItemId);

/// <summary>
/// Identifies a single recorded public forward by its forwarding message,
/// used as the dedupe/removal key for a given source.
/// </summary>
public readonly record struct ForwardRefKey(long ForwardingPeerId, int ForwardingMsgId);

/// <summary>
/// A recorded public forward of a source message or story.
/// </summary>
/// <param name="ForwardingPeerId">The public channel/chat (with a username) that forwarded the source.</param>
/// <param name="ForwardingMsgId">The message id of the forward inside the forwarding peer.</param>
/// <param name="OrderKey">A deterministic total-ordering key used as an opaque paging cursor.</param>
public sealed record PublicForwardRecord(long ForwardingPeerId, int ForwardingMsgId, long OrderKey);

/// <summary>
/// A single page of public forwards for a source.
/// </summary>
/// <param name="Count">The total number of currently-recorded, non-removed forwards for the source.</param>
/// <param name="Items">The forwards contained in this page, in the store's deterministic total order.</param>
/// <param name="NextOffset">A non-empty pagination cursor when more forwards remain, otherwise <c>null</c>.</param>
public sealed record PublicForwardPage(int Count, IReadOnlyList<PublicForwardRecord> Items, string? NextOffset);
