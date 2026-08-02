using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.SecretChat;

public class SecretChatMessageStore : ISecretChatMessageStore, ISingletonDependency
{
    private const string MessagesCollectionName = "encrypted_messages";
    private const string CountersCollectionName = "secret_qts_counters";

    private readonly IMongoDatabase _mongoDatabase;
    private readonly Lock _indexInitLock = new();
    private Task? _indexInit;

    public SecretChatMessageStore(IMongoDatabase mongoDatabase)
    {
        _mongoDatabase = mongoDatabase;
    }

    private IMongoCollection<EncryptedMessageDocument> Messages =>
        _mongoDatabase.GetCollection<EncryptedMessageDocument>(MessagesCollectionName);

    private IMongoCollection<BsonDocument> Counters =>
        _mongoDatabase.GetCollection<BsonDocument>(CountersCollectionName);

    /// <summary>
    /// How long an allocated-but-uncommitted qts holds the watermark down before it is stepped over.
    /// Overridable via object initialiser so tests can collapse the window; deliberately NOT a
    /// constructor parameter, because DI registration here is convention-based and a second ctor
    /// would make constructor selection ambiguous.
    /// </summary>
    public TimeSpan InflightStaleAfter { get; init; } = SecretChatConsts.QtsAllocationStaleAfter;

    public async Task<EncryptedMessageDocument?> FindAsync(long chatId, long senderUserId, long randomId)
    {
        var id = EncryptedMessageDocument.BuildId(chatId, senderUserId, randomId);

        return await Messages.Find(d => d.Id == id).FirstOrDefaultAsync();
    }

    public async Task<EncryptedMessageStoreResult> StoreAsync(EncryptedMessageDocument document)
    {
        await EnsureIndexesAsync();

        try
        {
            await Messages.InsertOneAsync(document);

            return new EncryptedMessageStoreResult(true, document);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var existing = await Messages.Find(d => d.Id == document.Id).FirstAsync();

            return new EncryptedMessageStoreResult(false, existing);
        }
    }

    public async Task<int> AllocateQtsAsync(long userId, long permAuthKeyId)
    {
        // Two pipeline stages in ONE round-trip, so allocating and registering the in-flight entry are
        // atomic: stage 2 observes stage 1's incremented Seq, so "q" is exactly the returned qts. Doing
        // this as two operations would reopen the very gap the Inflight set exists to close.
        var pipeline = new BsonDocumentStagePipelineDefinition<BsonDocument, BsonDocument>(
        [
            new BsonDocument("$set",
                new BsonDocument("Seq",
                    new BsonDocument("$add", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$Seq", 0 }), 1 }))),
            new BsonDocument("$set",
                new BsonDocument("Inflight",
                    new BsonDocument("$concatArrays", new BsonArray
                    {
                        new BsonDocument("$ifNull", new BsonArray { "$Inflight", new BsonArray() }),
                        new BsonArray
                        {
                            new BsonDocument
                            {
                                { "q", new BsonDocument("$add", new BsonArray { "$Seq", SecretChatConsts.QtsInitialValue - 1 }) },
                                { "t", "$$NOW" }
                            }
                        }
                    })))
        ]);

        var result = await Counters.FindOneAndUpdateAsync<BsonDocument>(
            Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(userId, permAuthKeyId)),
            new PipelineUpdateDefinition<BsonDocument>(pipeline),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        // First allocated value == QtsInitialValue.
        return SecretChatConsts.QtsInitialValue - 1 + result["Seq"].ToInt32();
    }

    /// <summary>
    /// Writes the qts onto the row and releases the allocation. Returns false when the row was already
    /// sequenced by a concurrent request, in which case the caller must NOT push again.
    /// </summary>
    public async Task<bool> SetQtsAsync(string id, int qts, long recipientUserId, long recipientPermAuthKeyId)
    {
        // Conditional on Qts == 0, matching the discipline AckAsync already uses: two racing requests that
        // both took the "finish the interrupted delivery" path must not both push the same message.
        var filterBuilder = Builders<EncryptedMessageDocument>.Filter;
        var rowUpdate = await Messages.UpdateOneAsync(
            filterBuilder.And(filterBuilder.Eq(d => d.Id, id), filterBuilder.Eq(d => d.Qts, 0)),
            Builders<EncryptedMessageDocument>.Update.Set(d => d.Qts, qts));

        if (rowUpdate.ModifiedCount != 1)
        {
            // Someone else sequenced this row. Release our allocation and burn the value: leaving the
            // entry in place would hold the watermark down for the whole staleness window.
            await AbandonQtsAsync(qts, recipientUserId, recipientPermAuthKeyId);

            return false;
        }

        // Advance the watermark and release the allocation in the same document update, so the qts
        // becomes advertisable at exactly the moment it becomes visible to GetForDifferenceAsync.
        // Stale entries are pruned opportunistically here, which keeps the array bounded without a sweeper.
        await Counters.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(recipientUserId, recipientPermAuthKeyId)),
            Builders<BsonDocument>.Update
                .Max("Delivered", qts)
                .PullFilter<BsonDocument, BsonDocument>("Inflight", Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.Eq("q", qts),
                    Builders<BsonDocument>.Filter.Lt("t", DateTime.UtcNow - InflightStaleAfter))),
            new UpdateOptions { IsUpsert = true });

        return true;
    }

    public Task AbandonQtsAsync(int qts, long recipientUserId, long recipientPermAuthKeyId)
    {
        return Counters.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(recipientUserId, recipientPermAuthKeyId)),
            Builders<BsonDocument>.Update.PullFilter<BsonDocument>("Inflight",
                Builders<BsonDocument>.Filter.Eq("q", qts)));
    }

    public async Task<int> GetHighestQtsAsync(long userId, long permAuthKeyId)
    {
        var counter = await Counters
            .Find(Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(userId, permAuthKeyId)))
            .FirstOrDefaultAsync();

        if (counter == null)
        {
            return SecretChatConsts.QtsInitialValue - 1;
        }

        var delivered = counter.TryGetValue("Delivered", out var deliveredValue)
            ? deliveredValue.ToInt32()
            : SecretChatConsts.QtsInitialValue - 1;

        // The invariant: every qts in (QtsInitialValue - 1, returned value] is already written onto its
        // row. "Delivered" alone cannot give that — it is a $max, so a later allocation that commits
        // first would carry it over an earlier allocation still in flight, and the client would advance
        // past a message GetForDifferenceAsync (filtering Qts > sinceQts) can never return again.
        // Clamping below the lowest live allocation closes that. Entries older than InflightStaleAfter
        // are ignored: their sender died between allocate and set, so the value is burnt rather than
        // wedging this device's watermark forever.
        var minInflight = MinLiveInflight(counter);

        return minInflight == null ? delivered : Math.Min(delivered, minInflight.Value - 1);
    }

    /// <summary>
    /// Highest qts ever handed out for the device, in-flight ones included. Distinct from
    /// <see cref="GetHighestQtsAsync"/>: <c>messages.receivedQueue</c> validates <c>max_qts</c> against
    /// this, because a client may already hold a live-pushed qts that the watermark has not reached yet.
    /// </summary>
    public async Task<int> GetAssignedQtsAsync(long userId, long permAuthKeyId)
    {
        var counter = await Counters
            .Find(Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(userId, permAuthKeyId)))
            .FirstOrDefaultAsync();

        if (counter == null || !counter.TryGetValue("Seq", out var seq))
        {
            return SecretChatConsts.QtsInitialValue - 1;
        }

        return SecretChatConsts.QtsInitialValue - 1 + seq.ToInt32();
    }

    /// <summary>Lowest still-live in-flight allocation, or null when none is outstanding.</summary>
    private int? MinLiveInflight(BsonDocument counter)
    {
        if (!counter.TryGetValue("Inflight", out var inflight) || inflight is not BsonArray entries)
        {
            return null;
        }

        // The cutoff mixes the app clock with the mongod clock that stamped "t" via $$NOW. Harmless at a
        // 60s bound, and $$NOW keeps a single authoritative writer clock however many replicas send.
        var cutoff = DateTime.UtcNow - InflightStaleAfter;
        int? min = null;

        foreach (var entry in entries.OfType<BsonDocument>())
        {
            if (!entry.TryGetValue("q", out var q) || !entry.TryGetValue("t", out var t))
            {
                continue;
            }

            if (t.IsValidDateTime && t.ToUniversalTime() < cutoff)
            {
                continue;
            }

            var value = q.ToInt32();
            if (min == null || value < min)
            {
                min = value;
            }
        }

        return min;
    }

    public async Task<IReadOnlyList<long>> AckAsync(long userId, long permAuthKeyId, int maxQts)
    {
        await EnsureIndexesAsync();

        var filterBuilder = Builders<EncryptedMessageDocument>.Filter;
        // Project away Data/File: an ack of a long backlog must not stream every blob back.
        var candidates = await Messages.Find(filterBuilder.And(
                filterBuilder.Eq(d => d.RecipientUserId, userId),
                filterBuilder.Eq(d => d.RecipientPermAuthKeyId, permAuthKeyId),
                filterBuilder.Eq(d => d.Acked, false),
                filterBuilder.Gt(d => d.Qts, 0),
                filterBuilder.Lte(d => d.Qts, maxQts)))
            .Sort(Builders<EncryptedMessageDocument>.Sort.Ascending(d => d.Qts))
            .Project(d => new AckCandidate(d.Id, d.RandomId))
            .ToListAsync();

        var ackedDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ackedRandomIds = new List<long>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // Per-row conditional update so "newly acked by THIS call" stays exact under concurrency.
            var updateResult = await Messages.UpdateOneAsync(
                filterBuilder.And(
                    filterBuilder.Eq(d => d.Id, candidate.Id),
                    filterBuilder.Eq(d => d.Acked, false)),
                Builders<EncryptedMessageDocument>.Update
                    .Set(d => d.Acked, true)
                    .Set(d => d.AckedDate, (int)ackedDate));

            if (updateResult.ModifiedCount == 1)
            {
                ackedRandomIds.Add(candidate.RandomId);
            }
        }

        return ackedRandomIds;
    }

    public async Task<IReadOnlyList<EncryptedMessageDocument>> GetForDifferenceAsync(long userId,
        long permAuthKeyId,
        int sinceQts,
        int limit,
        int maxQts = int.MaxValue)
    {
        await EnsureIndexesAsync();

        // A client-supplied qts below the initial value must not expose rows with Qts == 0
        // (sends interrupted between the insert and the qts assignment).
        if (sinceQts < SecretChatConsts.QtsInitialValue - 1)
        {
            sinceQts = SecretChatConsts.QtsInitialValue - 1;
        }

        var filterBuilder = Builders<EncryptedMessageDocument>.Filter;
        var find = Messages.Find(filterBuilder.And(
                filterBuilder.Eq(d => d.RecipientUserId, userId),
                filterBuilder.Eq(d => d.RecipientPermAuthKeyId, permAuthKeyId),
                filterBuilder.Eq(d => d.Acked, false),
                filterBuilder.Gt(d => d.Qts, sinceQts),
                // Never hand out a row above the watermark the same response advertises: on a truncated
                // page the caller reports the last returned qts as the cursor, which would silently skip
                // an allocation still in flight below it.
                filterBuilder.Lte(d => d.Qts, maxQts)))
            .Sort(Builders<EncryptedMessageDocument>.Sort.Ascending(d => d.Qts));

        if (limit > 0)
        {
            find = find.Limit(limit);
        }

        return await find.ToListAsync();
    }

    public Task DeleteByChatAsync(long chatId)
    {
        return Messages.DeleteManyAsync(d => d.ChatId == chatId);
    }

    private static string BuildCounterId(long userId, long permAuthKeyId)
    {
        return $"{userId}_{permAuthKeyId}";
    }

    /// <summary>
    /// Creates the indexes once. A failed attempt is NOT cached: caching a faulted task would make
    /// every subsequent send/ack/difference throw for the lifetime of the process after a single
    /// transient MongoDB failure.
    /// </summary>
    private Task EnsureIndexesAsync()
    {
        var pending = Volatile.Read(ref _indexInit);
        if (pending is { IsCompletedSuccessfully: true })
        {
            return pending;
        }

        lock (_indexInitLock)
        {
            if (_indexInit is null || _indexInit.IsFaulted || _indexInit.IsCanceled)
            {
                _indexInit = CreateIndexesAsync();
            }

            return _indexInit;
        }
    }

    private sealed record AckCandidate(string Id, long RandomId);

    private async Task CreateIndexesAsync()
    {
        var indexes = new[]
        {
            new CreateIndexModel<EncryptedMessageDocument>(
                Builders<EncryptedMessageDocument>.IndexKeys
                    .Ascending(d => d.RecipientUserId)
                    .Ascending(d => d.RecipientPermAuthKeyId)
                    .Ascending(d => d.Qts)),
            new CreateIndexModel<EncryptedMessageDocument>(
                Builders<EncryptedMessageDocument>.IndexKeys
                    .Ascending(d => d.RecipientUserId)
                    .Ascending(d => d.RecipientPermAuthKeyId)
                    .Ascending(d => d.Acked)
                    .Ascending(d => d.Qts)),
            new CreateIndexModel<EncryptedMessageDocument>(
                Builders<EncryptedMessageDocument>.IndexKeys.Ascending(d => d.ChatId)),
            // Retention, per "any messages older than 7 days may (and will) be deleted from the
            // server". Rows carry up to 8 MB of ciphertext each, so the queue cannot be kept forever.
            // Note the deliberate asymmetry: secret_qts_counters is NOT expired, so a device offline
            // past the window still sees the old "Delivered" watermark with the backing rows gone and
            // perceives a gap. That is the documented upstream behaviour — the client recovers via
            // decryptedMessageActionResend or aborts the secret chat.
            // Rows written before this field existed have no CreatedAt and are never expired, so no
            // backfill is required.
            new CreateIndexModel<EncryptedMessageDocument>(
                Builders<EncryptedMessageDocument>.IndexKeys.Ascending(d => d.CreatedAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) })
        };

        await Messages.Indexes.CreateManyAsync(indexes);
    }
}
