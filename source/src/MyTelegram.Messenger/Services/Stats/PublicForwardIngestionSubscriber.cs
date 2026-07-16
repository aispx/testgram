using EventFlow.Subscribers;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// Ingestion subscriber that feeds the <see cref="IPublicForwardStore"/> (Public_Forward_Store) from
/// existing message domain events (Requirements 11.1, 11.5, 11.6).
/// <para>
/// Write path:
/// <list type="bullet">
/// <item>
/// On <see cref="SendOutboxMessageCompletedSagaEvent"/>, when a channel-owned message is a forward of an
/// original <b>channel post</b> and the forwarding channel has a public username, the forward is recorded
/// against its source channel message. Deduplication is handled by the store on
/// <c>(source, forwardingPeerId, forwardingMsgId)</c> (Requirement 11.1); forwards into non-public chats
/// are never recorded because the public-username guard fails (Requirement 11.5).
/// </item>
/// <item>
/// On <see cref="ChannelMessageDeletedEvent"/>, when the deleted message carries the source reference
/// (<see cref="ChannelMessageDeletedEvent.PostChannelId"/>/<see cref="ChannelMessageDeletedEvent.PostMessageId"/>),
/// the corresponding recorded forward is soft-removed (Requirement 11.6).
/// </item>
/// </list>
/// </para>
/// <para>
/// Story public-forward ingestion (Requirement 11.2) is intentionally not wired here: stories are not
/// event-sourced through EventFlow aggregates in this codebase (they are persisted via the story document
/// store), so there are no story create/delete/repost domain events to subscribe to. See the class remarks
/// in the spec task notes for the recommended story-service-layer integration point.
/// </para>
/// </summary>
public class PublicForwardIngestionSubscriber(
    IPublicForwardStore publicForwardStore,
    IChannelAppService channelAppService,
    ILogger<PublicForwardIngestionSubscriber> logger)
    : ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent>,
        ISubscribeSynchronousTo<MessageAggregate, MessageId, ChannelMessageDeletedEvent>
{
    /// <summary>
    /// Records public forwards for each channel-owned forwarded message in a completed outbox send.
    /// </summary>
    public async Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        foreach (var item in domainEvent.AggregateEvent.MessageItems)
        {
            await TryRecordForwardAsync(item);
        }
    }

    /// <summary>
    /// Removes a recorded public forward when its forwarding channel message is deleted and the deletion
    /// event carries the original source-message reference.
    /// </summary>
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;

        // The deletion event only carries the original source reference for forwards whose forward header
        // preserved it (e.g. linked-channel/discussion forwards). When present, remove the matching record.
        if (!e.PostChannelId.HasValue || !e.PostMessageId.HasValue)
        {
            return;
        }

        var source = new ForwardSourceKey(ForwardSourceType.Message, e.PostChannelId.Value, e.PostMessageId.Value);
        var forwardRef = new ForwardRefKey(e.ChannelId, e.MessageId);

        await publicForwardStore.RemoveAsync(source, forwardRef);
    }

    private async Task TryRecordForwardAsync(MessageItem item)
    {
        // Only messages that live inside a channel can be a public forward held by a public channel.
        if (item.OwnerPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        // Must be a forward of an original channel post: FromId identifies the source channel and
        // ChannelPost identifies the source message id (the same identity used elsewhere to recognise
        // channel-post forwards).
        var fwd = item.FwdHeader;
        if (fwd?.FromId is not { PeerType: PeerType.Channel } fromChannel || !fwd.ChannelPost.HasValue)
        {
            return;
        }

        // Requirement 11.5: only record when the forwarding channel has a public username.
        var forwardingChannel = await channelAppService.GetAsync((long?)item.OwnerPeer.PeerId);
        if (forwardingChannel == null || string.IsNullOrEmpty(forwardingChannel.UserName))
        {
            return;
        }

        var source = new ForwardSourceKey(ForwardSourceType.Message, fromChannel.PeerId, fwd.ChannelPost.Value);
        var orderKey = ComputeOrderKey(item.Date, item.MessageId);
        var record = new PublicForwardRecord(item.OwnerPeer.PeerId, item.MessageId, orderKey);

        await publicForwardStore.RecordAsync(source, record);

        logger.LogDebug(
            "Recorded public forward of channel message {SourceChannelId}:{SourceMsgId} by public channel {FwdPeerId}:{FwdMsgId}",
            source.OwnerPeerId, source.ItemId, record.ForwardingPeerId, record.ForwardingMsgId);
    }

    /// <summary>
    /// Builds a deterministic total-ordering key from the forward's date and message id so pages are
    /// stably ordered (Requirement 11.4). Higher date sorts later; ties break on the forwarding message id.
    /// </summary>
    public static long ComputeOrderKey(int date, int messageId) =>
        ((long)(uint)date << 32) | (uint)messageId;
}
