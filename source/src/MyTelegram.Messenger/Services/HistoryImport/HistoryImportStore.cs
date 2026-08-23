using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Persistence of the chat imports. See https://corefork.telegram.org/api/import
/// </summary>
public interface IHistoryImportStore
{
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);

    Task<HistoryImportDocument?> GetAsync(long importId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The import of this peer that has not finished yet, if any. A second import into the same chat
    /// is refused while one is in flight.
    /// </summary>
    Task<HistoryImportDocument?> GetUnfinishedForPeerAsync(Peer peer,
        CancellationToken cancellationToken = default);

    /// <summary>Allocates an import id and stores the import together with its parsed messages.</summary>
    Task<HistoryImportDocument> CreateAsync(long userId, Peer peer, ChatExportFormat format, int mediaCount,
        int layer, IReadOnlyList<ImportedMessageLine> messages, CancellationToken cancellationToken = default);

    Task<List<HistoryImportMessageDocument>> ReadMessagesAsync(long importId, int fromSeq, int take,
        CancellationToken cancellationToken = default);

    Task SaveMediaAsync(long importId, string fileName, IMessageMedia media,
        CancellationToken cancellationToken = default);

    /// <summary>Uploaded media of an import, keyed by the file name the client sent.</summary>
    Task<Dictionary<string, IMessageMedia>> GetMediaAsync(long importId, IReadOnlyCollection<string> fileNames,
        CancellationToken cancellationToken = default);

    Task SetStatusAsync(long importId, HistoryImportStatus status, CancellationToken cancellationToken = default);

    Task SetProgressAsync(long importId, int importedCount, CancellationToken cancellationToken = default);

    /// <summary>Takes a lease on one queued import and marks it running.</summary>
    Task<HistoryImportDocument?> ClaimQueuedAsync(int leaseSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed run: below <paramref name="maxAttempts"/> the import goes back into the queue,
    /// otherwise it is marked failed.
    /// </summary>
    Task FailAsync(long importId, string error, int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>Drops the parsed messages and the uploaded media of a finished import.</summary>
    Task CleanupAsync(long importId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class HistoryImportStore(IMongoDatabase mongoDatabase, ILogger<HistoryImportStore> logger)
    : IHistoryImportStore, ITransientDependency
{
    public const string CollectionName = "history_imports";
    public const string MessagesCollectionName = "history_import_messages";
    public const string MediaCollectionName = "history_import_media";

    private const string CounterId = "history_import_id";

    /// <summary>Parsed messages are written in chunks so one insert cannot grow unbounded.</summary>
    private const int InsertBatchSize = 500;

    private IMongoCollection<HistoryImportDocument> Collection =>
        mongoDatabase.GetCollection<HistoryImportDocument>(CollectionName);

    private IMongoCollection<HistoryImportMessageDocument> Messages =>
        mongoDatabase.GetCollection<HistoryImportMessageDocument>(MessagesCollectionName);

    private IMongoCollection<HistoryImportMediaDocument> Media =>
        mongoDatabase.GetCollection<HistoryImportMediaDocument>(MediaCollectionName);

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        var imports = Builders<HistoryImportDocument>.IndexKeys;
        await Collection.Indexes.CreateManyAsync([
            new CreateIndexModel<HistoryImportDocument>(imports
                .Ascending(p => p.PeerType)
                .Ascending(p => p.PeerId)
                .Ascending(p => p.Status)),
            new CreateIndexModel<HistoryImportDocument>(imports.Ascending(p => p.Status)),
            // An import the client abandoned halfway through would otherwise keep its parsed
            // messages forever.
            new CreateIndexModel<HistoryImportDocument>(imports.Ascending(p => p.CreatedAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) })
        ], cancellationToken);

        // The same expiry as the import itself, so an abandoned import leaves nothing behind.
        await Messages.Indexes.CreateManyAsync([
            new CreateIndexModel<HistoryImportMessageDocument>(Builders<HistoryImportMessageDocument>.IndexKeys
                .Ascending(p => p.ImportId)
                .Ascending(p => p.Seq)),
            new CreateIndexModel<HistoryImportMessageDocument>(
                Builders<HistoryImportMessageDocument>.IndexKeys.Ascending(p => p.CreatedAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) })
        ], cancellationToken);

        await Media.Indexes.CreateManyAsync([
            new CreateIndexModel<HistoryImportMediaDocument>(Builders<HistoryImportMediaDocument>.IndexKeys
                .Ascending(p => p.ImportId)),
            new CreateIndexModel<HistoryImportMediaDocument>(
                Builders<HistoryImportMediaDocument>.IndexKeys.Ascending(p => p.CreatedAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) })
        ], cancellationToken);
    }

    public Task<HistoryImportDocument?> GetAsync(long importId, CancellationToken cancellationToken = default)
    {
        return Collection.Find(Builders<HistoryImportDocument>.Filter.Eq(p => p.Id, importId))
            .FirstOrDefaultAsync(cancellationToken)!;
    }

    public Task<HistoryImportDocument?> GetUnfinishedForPeerAsync(Peer peer,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<HistoryImportDocument>.Filter.And(
            Builders<HistoryImportDocument>.Filter.Eq(p => p.PeerId, peer.PeerId),
            Builders<HistoryImportDocument>.Filter.Eq(p => p.PeerType, peer.PeerType.ToString()),
            Builders<HistoryImportDocument>.Filter.In(p => p.Status,
                [HistoryImportStatus.Pending, HistoryImportStatus.Queued, HistoryImportStatus.Running]));

        return Collection.Find(filter)
            .Sort(Builders<HistoryImportDocument>.Sort.Descending(p => p.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken)!;
    }

    public async Task<HistoryImportDocument> CreateAsync(long userId, Peer peer, ChatExportFormat format,
        int mediaCount, int layer, IReadOnlyList<ImportedMessageLine> messages,
        CancellationToken cancellationToken = default)
    {
        var importId = await NextImportIdAsync(cancellationToken);
        var document = new HistoryImportDocument
        {
            Id = importId,
            UserId = userId,
            PeerId = peer.PeerId,
            PeerType = peer.PeerType.ToString(),
            Format = format.ToString(),
            MediaCount = mediaCount,
            TotalMessages = messages.Count,
            ImportedCount = 0,
            Status = HistoryImportStatus.Pending,
            Layer = layer,
            CreatedAt = DateTime.UtcNow
        };

        await Collection.InsertOneAsync(document, cancellationToken: cancellationToken);

        var batch = new List<HistoryImportMessageDocument>(InsertBatchSize);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            batch.Add(new HistoryImportMessageDocument
            {
                Id = $"{importId}_{i}",
                ImportId = importId,
                Seq = i,
                Date = message.Date,
                FromName = message.FromName,
                Text = message.Text,
                FileName = message.FileName
            });

            if (batch.Count < InsertBatchSize)
            {
                continue;
            }

            await Messages.InsertManyAsync(batch, cancellationToken: cancellationToken);
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await Messages.InsertManyAsync(batch, cancellationToken: cancellationToken);
        }

        return document;
    }

    public Task<List<HistoryImportMessageDocument>> ReadMessagesAsync(long importId, int fromSeq, int take,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<HistoryImportMessageDocument>.Filter.And(
            Builders<HistoryImportMessageDocument>.Filter.Eq(p => p.ImportId, importId),
            Builders<HistoryImportMessageDocument>.Filter.Gte(p => p.Seq, fromSeq));

        return Messages.Find(filter)
            .Sort(Builders<HistoryImportMessageDocument>.Sort.Ascending(p => p.Seq))
            .Limit(take)
            .ToListAsync(cancellationToken);
    }

    public Task SaveMediaAsync(long importId, string fileName, IMessageMedia media,
        CancellationToken cancellationToken = default)
    {
        var document = new HistoryImportMediaDocument
        {
            Id = BuildMediaId(importId, fileName),
            ImportId = importId,
            FileName = fileName,
            Media = media.ToBytes(),
            CreatedAt = DateTime.UtcNow
        };

        return Media.ReplaceOneAsync(Builders<HistoryImportMediaDocument>.Filter.Eq(p => p.Id, document.Id),
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<Dictionary<string, IMessageMedia>> GetMediaAsync(long importId,
        IReadOnlyCollection<string> fileNames, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IMessageMedia>(StringComparer.OrdinalIgnoreCase);
        if (fileNames.Count == 0)
        {
            return result;
        }

        var ids = fileNames.Select(p => BuildMediaId(importId, p)).Distinct().ToList();
        var documents = await Media.Find(Builders<HistoryImportMediaDocument>.Filter.In(p => p.Id, ids))
            .ToListAsync(cancellationToken);

        foreach (var document in documents)
        {
            try
            {
                var media = ((ReadOnlyMemory<byte>)document.Media).ToTObject<IMessageMedia>();
                result[document.FileName] = media;
            }
            catch (Exception ex)
            {
                // A media blob we can no longer read must not sink the whole import; the message is
                // imported as text instead.
                logger.LogWarning(ex, "Could not read the imported media {FileName} of import {ImportId}",
                    document.FileName, importId);
            }
        }

        return result;
    }

    public Task SetStatusAsync(long importId, HistoryImportStatus status,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<HistoryImportDocument>.Update.Set(p => p.Status, status);
        update = status switch
        {
            HistoryImportStatus.Running => update.Set(p => p.StartedAt, DateTime.UtcNow),
            HistoryImportStatus.Completed or HistoryImportStatus.Failed => update
                .Set(p => p.FinishedAt, DateTime.UtcNow)
                .Set(p => p.ClaimedUntil, null),
            _ => update
        };

        return Collection.UpdateOneAsync(Builders<HistoryImportDocument>.Filter.Eq(p => p.Id, importId), update,
            cancellationToken: cancellationToken);
    }

    public Task SetProgressAsync(long importId, int importedCount, CancellationToken cancellationToken = default)
    {
        return Collection.UpdateOneAsync(Builders<HistoryImportDocument>.Filter.Eq(p => p.Id, importId),
            Builders<HistoryImportDocument>.Update
                .Set(p => p.ImportedCount, importedCount)
                // The lease has to move with the progress, otherwise a long import is stolen halfway.
                .Set(p => p.ClaimedUntil, DateTime.UtcNow.AddMinutes(10)),
            cancellationToken: cancellationToken);
    }

    public Task<HistoryImportDocument?> ClaimQueuedAsync(int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var filter = Builders<HistoryImportDocument>.Filter.And(
            Builders<HistoryImportDocument>.Filter.In(p => p.Status,
                [HistoryImportStatus.Queued, HistoryImportStatus.Running]),
            Builders<HistoryImportDocument>.Filter.Or(
                Builders<HistoryImportDocument>.Filter.Eq(p => p.ClaimedUntil, null),
                Builders<HistoryImportDocument>.Filter.Lt(p => p.ClaimedUntil, now)));

        return Collection.FindOneAndUpdateAsync(filter,
            Builders<HistoryImportDocument>.Update
                .Set(p => p.ClaimedUntil, now.AddSeconds(leaseSeconds))
                .Set(p => p.Status, HistoryImportStatus.Running)
                .Set(p => p.StartedAt, now),
            new FindOneAndUpdateOptions<HistoryImportDocument>
            {
                Sort = Builders<HistoryImportDocument>.Sort.Ascending(p => p.CreatedAt),
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken)!;
    }

    public async Task FailAsync(long importId, string error, int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var document = await GetAsync(importId, cancellationToken);
        var attempts = (document?.Attempts ?? 0) + 1;
        var giveUp = attempts >= maxAttempts;

        await Collection.UpdateOneAsync(Builders<HistoryImportDocument>.Filter.Eq(p => p.Id, importId),
            Builders<HistoryImportDocument>.Update
                .Set(p => p.Attempts, attempts)
                .Set(p => p.LastError, error)
                .Set(p => p.ClaimedUntil, null)
                .Set(p => p.Status, giveUp ? HistoryImportStatus.Failed : HistoryImportStatus.Queued)
                .Set(p => p.FinishedAt, giveUp ? DateTime.UtcNow : null),
            cancellationToken: cancellationToken);
    }

    public async Task CleanupAsync(long importId, CancellationToken cancellationToken = default)
    {
        await Messages.DeleteManyAsync(
            Builders<HistoryImportMessageDocument>.Filter.Eq(p => p.ImportId, importId), cancellationToken);
        await Media.DeleteManyAsync(Builders<HistoryImportMediaDocument>.Filter.Eq(p => p.ImportId, importId),
            cancellationToken);
    }

    private static string BuildMediaId(long importId, string fileName)
    {
        return $"{importId}_{fileName.ToLowerInvariant()}";
    }

    private async Task<long> NextImportIdAsync(CancellationToken cancellationToken)
    {
        var result = await mongoDatabase.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", CounterId),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);

        return result["seq"].ToInt64();
    }
}
