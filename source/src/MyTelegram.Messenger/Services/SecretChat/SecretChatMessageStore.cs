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
        var result = await Counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(userId, permAuthKeyId)),
            Builders<BsonDocument>.Update.Inc("Seq", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        // First allocated value == QtsInitialValue.
        return SecretChatConsts.QtsInitialValue - 1 + result["Seq"].ToInt32();
    }

    public async Task SetQtsAsync(string id, int qts, long recipientUserId, long recipientPermAuthKeyId)
    {
        await Messages.UpdateOneAsync(d => d.Id == id,
            Builders<EncryptedMessageDocument>.Update.Set(d => d.Qts, qts));

        // Publish the watermark only AFTER the row carries its qts. GetHighestQtsAsync reads
        // Delivered, never Seq, so updates.getState/getDifference can never advertise a qts whose
        // row is still invisible to GetForDifferenceAsync (which filters on Qts > sinceQts).
        await Counters.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(recipientUserId, recipientPermAuthKeyId)),
            Builders<BsonDocument>.Update.Max("Delivered", qts),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task<int> GetHighestQtsAsync(long userId, long permAuthKeyId)
    {
        var counter = await Counters
            .Find(Builders<BsonDocument>.Filter.Eq("_id", BuildCounterId(userId, permAuthKeyId)))
            .FirstOrDefaultAsync();

        // Deliberately NOT "Seq": a value that has been allocated but whose message row has not yet
        // been made visible must never be advertised to the client, or the client would advance past
        // a message it can no longer fetch via updates.getDifference.
        if (counter == null || !counter.TryGetValue("Delivered", out var delivered))
        {
            return SecretChatConsts.QtsInitialValue - 1;
        }

        return delivered.ToInt32();
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
        int limit)
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
                filterBuilder.Gt(d => d.Qts, sinceQts)))
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
                Builders<EncryptedMessageDocument>.IndexKeys.Ascending(d => d.ChatId))
        };

        await Messages.Indexes.CreateManyAsync(indexes);
    }
}
