using MongoDB.Driver;
using MyTelegram.Domain.Shared;

namespace MyTelegram.Messenger.Services.Scheduled;

/// <inheritdoc />
public class ScheduledMessageStore(
    IMongoDatabase mongoDatabase,
    IMessageConverterService messageConverterService,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IUserAppService userAppService,
    IPeerHelper peerHelper,
    IPrivacyAppService privacyAppService,
    IMessageEncryptionHelper messageEncryptionHelper)
    : IScheduledMessageStore, ITransientDependency
{
    public const string CollectionName = "scheduled_messages";

    private IMongoCollection<ScheduledMessageDocument> Collection =>
        mongoDatabase.GetCollection<ScheduledMessageDocument>(CollectionName);

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var builder = Builders<ScheduledMessageDocument>.IndexKeys;
        await Collection.Indexes.CreateManyAsync([
                new CreateIndexModel<ScheduledMessageDocument>(builder.Ascending(p => p.ScheduleDate)),
                new CreateIndexModel<ScheduledMessageDocument>(builder
                    .Ascending(p => p.PeerType)
                    .Ascending(p => p.PeerId)
                    .Ascending(p => p.SenderUserId)),
                new CreateIndexModel<ScheduledMessageDocument>(builder
                    .Ascending(p => p.PeerType)
                    .Ascending(p => p.PeerId)
                    .Ascending(p => p.ScheduledMessageId))
            ],
            cancellationToken);
    }

    public async Task<bool> CheckQueueAccessAsync(Peer peer, long selfUserId)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return false;
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);

        // A broadcast channel has a single queue: every admin allowed to post sees the scheduled posts
        // of the other admins too, exactly like the official clients show them.
        if (channelReadModel is not { Broadcast: true })
        {
            return false;
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, selfUserId, p => p.PostMessages,
            RpcErrors.RpcErrors400.ChatAdminRequired);

        return true;
    }

    public async Task<IReadOnlyList<long>> GetQueueAudienceAsync(Peer peer, long senderUserId)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return [senderUserId];
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel is not { Broadcast: true })
        {
            return [senderUserId];
        }

        // The shared queue of a broadcast channel is visible to the creator and every admin allowed to
        // post; the author is always included even if their rights changed after they queued the post.
        var audience = new HashSet<long> { senderUserId, channelReadModel.CreatorId };
        foreach (var admin in channelReadModel.AdminList)
        {
            if (admin.AdminRights.PostMessages)
            {
                audience.Add(admin.UserId);
            }
        }

        return [.. audience];
    }

    public async Task<List<ScheduledMessageDocument>> GetQueueAsync(Peer peer, long selfUserId, bool sharedQueue,
        IReadOnlyList<int>? scheduledMessageIds = null)
    {
        var filters = new List<FilterDefinition<ScheduledMessageDocument>>
        {
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.PeerId, peer.PeerId),
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.PeerType, peer.PeerType.ToString()),
            ReadableFilter()
        };

        if (!sharedQueue)
        {
            filters.Add(Builders<ScheduledMessageDocument>.Filter.Eq(p => p.SenderUserId, selfUserId));
        }

        if (scheduledMessageIds != null)
        {
            filters.Add(Builders<ScheduledMessageDocument>.Filter.In(p => p.ScheduledMessageId, scheduledMessageIds));
        }

        return await Collection
            .Find(Builders<ScheduledMessageDocument>.Filter.And(filters))
            .Sort(Builders<ScheduledMessageDocument>.Sort.Ascending(p => p.ScheduleDate)
                .Ascending(p => p.ScheduledMessageId))
            .ToListAsync();
    }

    public Task<long> CountAsync(Peer peer, long senderUserId)
    {
        return Collection.CountDocumentsAsync(Builders<ScheduledMessageDocument>.Filter.And(
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.PeerId, peer.PeerId),
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.PeerType, peer.PeerType.ToString()),
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.SenderUserId, senderUserId)));
    }

    public async Task<List<ScheduledMessageDocument>> SaveAsync(IReadOnlyList<ScheduledQueueItem> items,
        RequestInfo requestInfo)
    {
        var documents = items.Select(p => CreateDocument(p, requestInfo)).ToList();
        if (documents.Count > 0)
        {
            await Collection.InsertManyAsync(documents);
        }

        return documents;
    }

    public Task ReplaceAsync(ScheduledMessageDocument document)
    {
        return Collection.ReplaceOneAsync(
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.Id, document.Id),
            document,
            new ReplaceOptions { IsUpsert = true });
    }

    public Task DeleteAsync(IEnumerable<string> documentIds)
    {
        return Collection.DeleteManyAsync(Builders<ScheduledMessageDocument>.Filter.In(p => p.Id, documentIds));
    }

    public Task<List<ScheduledMessageDocument>> ClaimDueAsync(int now, int limit, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ScheduledMessageDocument>.Filter.And(
            Builders<ScheduledMessageDocument>.Filter.Lte(p => p.ScheduleDate, now),
            Builders<ScheduledMessageDocument>.Filter.Ne(p => p.ScheduleDate, ScheduledMessageRules.WhenOnlineDate),
            // An entry whose video is still converting waits for the converter, not for the clock.
            Builders<ScheduledMessageDocument>.Filter.Ne(p => p.VideoProcessingPending, true),
            ReadableFilter(),
            NotClaimedFilter(now));

        return ClaimAsync(filter, limit, leaseSeconds, cancellationToken);
    }

    public Task<List<ScheduledMessageDocument>> ClaimVideoProcessingAsync(int limit, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow.ToTimestamp();
        var filter = Builders<ScheduledMessageDocument>.Filter.And(
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.VideoProcessingPending, true),
            ReadableFilter(),
            NotClaimedFilter(now));

        return ClaimAsync(filter, limit, leaseSeconds, cancellationToken);
    }

    public Task<List<ScheduledMessageDocument>> ClaimWhenOnlineAsync(IReadOnlyCollection<long> onlineUserIds, int limit,
        int leaseSeconds, CancellationToken cancellationToken = default)
    {
        if (onlineUserIds.Count == 0)
        {
            return Task.FromResult(new List<ScheduledMessageDocument>());
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filter = Builders<ScheduledMessageDocument>.Filter.And(
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.ScheduleDate, ScheduledMessageRules.WhenOnlineDate),
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.PeerType, PeerType.User.ToString()),
            Builders<ScheduledMessageDocument>.Filter.In(p => p.PeerId, onlineUserIds),
            Builders<ScheduledMessageDocument>.Filter.Ne(p => p.VideoProcessingPending, true),
            ReadableFilter(),
            NotClaimedFilter(now));

        return ClaimAsync(filter, limit, leaseSeconds, cancellationToken);
    }

    public async Task<int?> GetNextScheduleDateAsync(CancellationToken cancellationToken = default)
    {
        var document = await Collection
            .Find(Builders<ScheduledMessageDocument>.Filter.And(
                Builders<ScheduledMessageDocument>.Filter.Ne(p => p.ScheduleDate,
                    ScheduledMessageRules.WhenOnlineDate),
                Builders<ScheduledMessageDocument>.Filter.Ne(p => p.VideoProcessingPending, true),
                ReadableFilter()))
            .Sort(Builders<ScheduledMessageDocument>.Sort.Ascending(p => p.ScheduleDate))
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken);

        return document?.ScheduleDate;
    }

    public Task ReleaseAsync(ScheduledMessageDocument document, int nextAttemptDate)
    {
        return Collection.UpdateOneAsync(
            Builders<ScheduledMessageDocument>.Filter.Eq(p => p.Id, document.Id),
            Builders<ScheduledMessageDocument>.Update
                .Set(p => p.ClaimedUntil, null)
                .Set(p => p.Attempts, document.Attempts + 1)
                .Set(p => p.NextAttemptDate, nextAttemptDate));
    }

    public IMessage Render(ScheduledMessageDocument document, long selfUserId, int layer)
    {
        var message = messageConverterService.ToMessage(selfUserId, document.Item, layer: layer);
        message.Id = document.ScheduledMessageId;

        // The message in the queue is dated at the moment it will be sent, not at the moment it was
        // queued: see https://corefork.telegram.org/api/scheduled-messages
        if (message is ILayeredMessage layeredMessage)
        {
            layeredMessage.Date = document.ScheduleDate;
        }

        if (message is MyTelegram.Schema.TMessage latestMessage)
        {
            latestMessage.EditDate = document.EditDate;
            latestMessage.ScheduleRepeatPeriod = document.RepeatPeriod;
            latestMessage.VideoProcessingPending = document.VideoProcessingPending;
        }

        return message;
    }

    public TUpdates BuildNewScheduledUpdates(IReadOnlyList<ScheduledMessageDocument> documents, long selfUserId,
        int layer)
    {
        var updates = new TVector<IUpdate>();
        foreach (var document in documents)
        {
            updates.Add(new TUpdateNewScheduledMessage { Message = Render(document, selfUserId, layer) });
        }

        return new TUpdates
        {
            Updates = updates,
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }

    public TUpdates BuildDeleteScheduledUpdates(Peer peer, IReadOnlyList<int> scheduledMessageIds,
        IReadOnlyList<int>? sentMessageIds = null)
    {
        var update = new TUpdateDeleteScheduledMessages
        {
            Peer = peerHelper.ToPeer(peer),
            Messages = new TVector<int>(scheduledMessageIds)
        };

        if (sentMessageIds is { Count: > 0 })
        {
            update.SentMessages = new TVector<int>(sentMessageIds);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { update },
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }

    public SendMessageInput BuildSendInput(ScheduledMessageDocument document, RequestInfo requestInfo, int messageId,
        int groupItemCount = 1)
    {
        var item = document.Item;

        // The queue stores the text as ciphertext when encryption is on (see CreateDocument); the send
        // pipeline re-encrypts from plaintext, so decrypt it back here first.
        var message = item.Message;
        if (item.EncryptedData is { Length: > 0 })
        {
            message = messageEncryptionHelper.Decrypt(item.OwnerPeer.PeerId, item.MessageId, item.EncryptedData.Value);
        }

        return new SendMessageInput(requestInfo,
            document.SenderUserId,
            item.ToPeer,
            message,
            document.RandomId,
            entities: item.Entities,
            inputReplyTo: item.InputReplyTo,
            media: item.Media,
            sendMessageType: item.SendMessageType,
            messageType: item.MessageType,
            messageAction: item.MessageAction,
            groupId: item.GroupId,
            // Only an album has a meaningful item count; a batch of unrelated messages flushed together
            // must not look like one.
            groupItemCount: item.GroupId.HasValue ? groupItemCount : 1,
            pollId: item.PollId,
            replyMarkup: item.ReplyMarkup,
            topMsgId: item.TopMsgId,
            sendAs: item.SendAs,
            effect: item.Effect,
            isSendGroupedMessage: item.GroupId.HasValue,
            silent: item.Silent,
            invertMedia: item.InvertMedia,
            paidMessageStars: item.PaidMessageStars,
            ttlPeriod: item.TtlPeriod,
            savedPeerId: item.SavedPeerId,
            messageId: messageId,
            suggestedPost: item.SuggestedPost,
            fwdHeader: item.FwdHeader,
            messageSubType: item.MessageSubType,
            postAuthor: item.PostAuthor,
            views: item.Views,
            noForwards: item.NoForwards,
            fromScheduled: true);
    }

    public async Task ValidateAsync(long senderUserId, Peer toPeer, int scheduleDate, int? repeatPeriod, int batchSize)
    {
        var now = DateTime.UtcNow.ToTimestamp();
        ScheduledMessageRules.ValidateDate(scheduleDate, now);
        ScheduledMessageRules.ValidateRepeatPeriod(repeatPeriod, batchSize);

        // Bots cannot schedule messages at all.
        if (peerHelper.IsBotUser(senderUserId))
        {
            RpcErrors.RpcErrors400.ScheduleBotNotAllowed.ThrowRpcError();
        }

        if (repeatPeriod.HasValue)
        {
            await userAppService.CheckAccountPremiumStatusAsync(senderUserId);
        }

        if (ScheduledMessageRules.IsWhenOnline(scheduleDate))
        {
            // "only if the destination is a private chat with a user"
            if (toPeer.PeerType != PeerType.User || toPeer.PeerId == senderUserId)
            {
                RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
            }

            // Waiting for someone to come online is impossible when their last seen time is hidden.
            await privacyAppService.ApplyPrivacyAsync(senderUserId, toPeer.PeerId,
                _ => RpcErrors.RpcErrors400.ScheduleStatusPrivate.ThrowRpcError(),
                PrivacyType.StatusTimestamp);
        }

        if (await CountAsync(toPeer, senderUserId) + batchSize > ScheduledMessageRules.MaxQueuedMessagesPerPeer)
        {
            RpcErrors.RpcErrors400.ScheduleTooMuch.ThrowRpcError();
        }
    }

    private static ScheduledMessageDocument CreateDocument(ScheduledQueueItem queueItem, RequestInfo requestInfo)
    {
        var item = queueItem.Item;
        var scheduledMessageId = item.ScheduleMessageId ?? item.MessageId;

        // When message encryption is on, MessageAppService already produced the ciphertext in
        // EncryptedData. Keep it and blank the plaintext, so the text is never stored in clear while it
        // waits in the queue; the flush path decrypts it back and the render path (ToMessage) already
        // decrypts EncryptedData for the client. When encryption is off, EncryptedData is null and the
        // plaintext stays as-is.
        var hasCiphertext = item.EncryptedData is { Length: > 0 };

        return new ScheduledMessageDocument
        {
            Id = BuildDocumentId(item.OwnerPeer.PeerId, scheduledMessageId),
            ScheduledMessageId = scheduledMessageId,
            OwnerPeerId = item.OwnerPeer.PeerId,
            SenderUserId = item.SenderUserId,
            PeerId = item.ToPeer.PeerId,
            PeerType = item.ToPeer.PeerType.ToString(),
            ScheduleDate = item.ScheduleDate!.Value,
            RepeatPeriod = queueItem.RepeatPeriod,
            PreallocatedMessageId = queueItem.PreallocatedMessageId,
            VideoProcessingPending = queueItem.VideoProcessingPending,
            // The schedule fields live on the document, not on the queued item. The inbox copy of the
            // ciphertext is dropped: it is re-derived for the recipient when the message is sent.
            Item = item with
            {
                ScheduleDate = null,
                ScheduleMessageId = null,
                Message = hasCiphertext ? string.Empty : item.Message,
                InboxMessageEncryptedData = null
            },
            Layer = requestInfo.Layer,
            RandomId = item.RandomId,
            CreatedAt = DateTime.UtcNow
        };
    }

    public static string BuildDocumentId(long ownerPeerId, int scheduledMessageId)
    {
        return $"scheduled-{ownerPeerId}-{scheduledMessageId}";
    }

    /// <summary>
    /// Entries written before the queue stored the whole <see cref="MessageItem"/> cannot be rendered or
    /// sent any more; they are ignored instead of breaking every read of the queue.
    /// </summary>
    private static FilterDefinition<ScheduledMessageDocument> ReadableFilter()
    {
        return Builders<ScheduledMessageDocument>.Filter.Exists(p => p.Item);
    }

    private static FilterDefinition<ScheduledMessageDocument> NotClaimedFilter(int now)
    {
        return Builders<ScheduledMessageDocument>.Filter.And(
            Builders<ScheduledMessageDocument>.Filter.Or(
                Builders<ScheduledMessageDocument>.Filter.Eq(p => p.ClaimedUntil, null),
                Builders<ScheduledMessageDocument>.Filter.Lt(p => p.ClaimedUntil, DateTime.UtcNow)),
            Builders<ScheduledMessageDocument>.Filter.Or(
                Builders<ScheduledMessageDocument>.Filter.Eq(p => p.NextAttemptDate, null),
                Builders<ScheduledMessageDocument>.Filter.Lte(p => p.NextAttemptDate, now)));
    }

    private async Task<List<ScheduledMessageDocument>> ClaimAsync(FilterDefinition<ScheduledMessageDocument> filter,
        int limit, int leaseSeconds, CancellationToken cancellationToken)
    {
        var claimed = new List<ScheduledMessageDocument>();
        var options = new FindOneAndUpdateOptions<ScheduledMessageDocument>
        {
            Sort = Builders<ScheduledMessageDocument>.Sort.Ascending(p => p.ScheduleDate),
            ReturnDocument = ReturnDocument.After
        };

        for (var i = 0; i < limit; i++)
        {
            // One document at a time: the lease is what stops a second command server from sending the
            // same queued message again.
            var document = await Collection.FindOneAndUpdateAsync(filter,
                Builders<ScheduledMessageDocument>.Update.Set(p => p.ClaimedUntil,
                    DateTime.UtcNow.AddSeconds(leaseSeconds)),
                options,
                cancellationToken);

            if (document == null)
            {
                break;
            }

            claimed.Add(document);
        }

        return claimed;
    }
}
