namespace MyTelegram.Messenger.Services.Scheduled;

/// <inheritdoc />
public class ScheduledMessageDispatcher(
    IScheduledMessageStore store,
    IMessageAppService messageAppService,
    IIdGenerator idGenerator,
    IObjectMessageSender objectMessageSender,
    ILogger<ScheduledMessageDispatcher> logger)
    : IScheduledMessageDispatcher, ITransientDependency
{
    public async Task<IUpdates> FlushAsync(IReadOnlyList<ScheduledMessageDocument> documents,
        RequestInfo? requestInfo = null)
    {
        var updates = new TVector<IUpdate>();
        if (documents.Count == 0)
        {
            return BuildUpdates(updates);
        }

        var sentMessageIds = new Dictionary<string, int>();

        // An album has to stay one send call, so the grouped messages keep their grouped_id semantics.
        foreach (var group in documents.GroupBy(p => new { p.SenderUserId, p.PeerId, p.PeerType, p.Item.GroupId }))
        {
            var groupDocuments = group.ToList();
            var groupRequestInfo = BuildRequestInfo(groupDocuments[0], requestInfo);
            var inputs = new List<SendMessageInput>();

            foreach (var document in groupDocuments)
            {
                // The id inside the queue comes from the same per peer sequence as real message ids, but a
                // message that is actually sent needs a fresh one so it lands at the end of the history.
                var messageId = document.PreallocatedMessageId
                                ?? await idGenerator.NextIdAsync(IdType.MessageId, document.OwnerPeerId);
                sentMessageIds[document.Id] = messageId;
                inputs.Add(store.BuildSendInput(document, groupRequestInfo, messageId, groupDocuments.Count));
            }

            await messageAppService.SendMessageAsync(inputs);
        }

        foreach (var peerGroup in documents.GroupBy(p => new { p.SenderUserId, p.PeerId, p.PeerType }))
        {
            var peerDocuments = peerGroup.ToList();
            var peer = peerDocuments[0].Item.ToPeer;
            var senderPeer = new Peer(PeerType.User, peerGroup.Key.SenderUserId);

            var deleteUpdates = store.BuildDeleteScheduledUpdates(peer,
                peerDocuments.Select(p => p.ScheduledMessageId).ToList(),
                peerDocuments.Select(p => sentMessageIds[p.Id]).ToList());

            foreach (var update in deleteUpdates.Updates)
            {
                updates.Add(update);
            }

            await store.DeleteAsync(peerDocuments.Select(p => p.Id));

            // Other sessions of the sender always need the update; the session that asked for the flush
            // gets it as the rpc result instead.
            await objectMessageSender.PushMessageToPeerAsync(senderPeer, deleteUpdates,
                excludeAuthKeyId: requestInfo?.AuthKeyId);

            var repeated = await RescheduleRepeatingAsync(peerDocuments);
            if (repeated.Count > 0)
            {
                var newScheduledUpdates = store.BuildNewScheduledUpdates(repeated, peerGroup.Key.SenderUserId,
                    peerDocuments[0].Layer);
                await objectMessageSender.PushMessageToPeerAsync(senderPeer, newScheduledUpdates);
            }
        }

        return BuildUpdates(updates);
    }

    /// <summary>
    /// A repeating message is put back into the queue right after it was sent, "as if we had re-invoked
    /// sendMessage with a schedule_date equal to the current time plus schedule_repeat_period".
    /// </summary>
    private async Task<List<ScheduledMessageDocument>> RescheduleRepeatingAsync(
        IReadOnlyList<ScheduledMessageDocument> documents)
    {
        var repeated = new List<ScheduledMessageDocument>();
        foreach (var document in documents)
        {
            if (!document.RepeatPeriod.HasValue)
            {
                continue;
            }

            var scheduledMessageId = await idGenerator.NextIdAsync(IdType.MessageId, document.OwnerPeerId);
            var randomId = Random.Shared.NextInt64();
            var next = new ScheduledMessageDocument
            {
                Id = ScheduledMessageStore.BuildDocumentId(document.OwnerPeerId, scheduledMessageId),
                ScheduledMessageId = scheduledMessageId,
                OwnerPeerId = document.OwnerPeerId,
                SenderUserId = document.SenderUserId,
                PeerId = document.PeerId,
                PeerType = document.PeerType,
                ScheduleDate = DateTime.UtcNow.ToTimestamp() + document.RepeatPeriod.Value,
                RepeatPeriod = document.RepeatPeriod,
                Item = document.Item with { RandomId = randomId },
                Layer = document.Layer,
                RandomId = randomId,
                CreatedAt = DateTime.UtcNow
            };

            await store.ReplaceAsync(next);
            repeated.Add(next);

            logger.LogInformation(
                "Repeating scheduled message {ScheduledMessageId} re-queued for user {UserId} at {Date}",
                next.ScheduledMessageId, next.SenderUserId, next.ScheduleDate);
        }

        return repeated;
    }

    private static RequestInfo BuildRequestInfo(ScheduledMessageDocument document, RequestInfo? requestInfo)
    {
        // ReqMsgId is dropped: the rpc result of the triggering request is produced by the caller, the
        // send pipeline must not answer it a second time.
        if (requestInfo != null)
        {
            return requestInfo with { ReqMsgId = 0 };
        }

        return new RequestInfo(
            ConnectionId: "scheduled",
            SessionId: 0,
            ReqMsgId: 0,
            UserId: document.SenderUserId,
            AccessHashKeyId: 0,
            AuthKeyId: 0,
            PermAuthKeyId: 0,
            RequestId: Guid.NewGuid(),
            Layer: document.Layer,
            Date: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static IUpdates BuildUpdates(TVector<IUpdate> updates)
    {
        return new TUpdates
        {
            Updates = updates,
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }
}
