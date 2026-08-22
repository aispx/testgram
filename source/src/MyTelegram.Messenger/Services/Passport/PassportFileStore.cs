using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Mongo-backed <see cref="IPassportFileStore"/>. See the interface for the contract.
/// </summary>
public class PassportFileStore(
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options) : IPassportFileStore, ISingletonDependency
{
    private const string CollectionName = "passport_files";
    private const string PartsCollectionName = "passport_file_parts";
    private const string UploadPartsCollectionName = "file_parts";

    private readonly Lock _indexInitLock = new();
    private Task? _indexInit;

    private IMongoCollection<PassportFileDocument> Collection =>
        mongoDatabase.GetCollection<PassportFileDocument>(CollectionName);

    private IMongoCollection<PassportFilePartDocument> Parts =>
        mongoDatabase.GetCollection<PassportFilePartDocument>(PartsCollectionName);

    public async Task<PassportFileDocument> StoreUploadedAsync(long userId,
        long clientFileId,
        int declaredParts,
        string? md5Checksum,
        byte[] fileHash,
        byte[] secret)
    {
        await EnsureIndexesAsync();

        var uploadedParts = await LoadUploadedPartsAsync(userId, clientFileId);

        if (uploadedParts.Count == 0)
        {
            RpcErrors.RpcErrors400.FileEmtpy.ThrowRpcError();
        }

        if (declaredParts > 0 && uploadedParts.Count != declaredParts)
        {
            RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
        }

        // The parts must form a contiguous 0..n-1 run, otherwise a silently missing part would be
        // concatenated away and the document would decrypt to garbage on the service's side.
        for (var index = 0; index < uploadedParts.Count; index++)
        {
            if (uploadedParts[index]["FilePart"].ToInt32() != index)
            {
                RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
            }
        }

        var blob = AssembleBlob(uploadedParts);

        if (blob.LongLength > options.CurrentValue.Passport.MaxFileSizeBytes)
        {
            RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
        }

        // As for secret chat files, the md5_checksum covers the ENCRYPTED blob and is the only
        // integrity check the server can perform - the plaintext hash (file_hash) is not verifiable
        // here, since the server has no key. https://corefork.telegram.org/passport/encryption
        if (!string.IsNullOrEmpty(md5Checksum))
        {
            var actualMd5 = Convert.ToHexString(MD5.HashData(blob)).ToLowerInvariant();
            if (!string.Equals(actualMd5, md5Checksum, StringComparison.OrdinalIgnoreCase))
            {
                RpcErrors.RpcErrors400.Md5ChecksumInvalid.ThrowRpcError();
            }
        }

        var document = new PassportFileDocument
        {
            Id = NewNonZeroId(),
            AccessHash = NewNonZeroId(),
            Size = blob.LongLength,
            DcId = options.CurrentValue.ThisDcId > 0 ? options.CurrentValue.ThisDcId : 1,
            OwnerUserId = userId,
            SourceFileId = clientFileId,
            FileHash = fileHash,
            Secret = secret,
            Parts = uploadedParts.Count,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        while (true)
        {
            try
            {
                await Collection.InsertOneAsync(document);
                break;
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                document.Id = NewNonZeroId();
            }
        }

        // Immutable snapshot: file_parts is keyed by the CLIENT-chosen file id and is upserted, so a
        // client reusing that id would otherwise rewrite the bytes of an already-saved document.
        var offset = 0L;
        var snapshots = new List<PassportFilePartDocument>(uploadedParts.Count);
        for (var index = 0; index < uploadedParts.Count; index++)
        {
            var bytes = uploadedParts[index]["Bytes"].AsByteArray;
            snapshots.Add(new PassportFilePartDocument
            {
                Id = BuildPartId(document.Id, index),
                FileId = document.Id,
                PartIndex = index,
                Offset = offset,
                Bytes = bytes
            });
            offset += bytes.Length;
        }

        await Parts.InsertManyAsync(snapshots);

        return document;
    }

    public async Task<PassportFileDocument?> GetAsync(long fileId, long ownerUserId)
    {
        return await Collection
            .Find(d => d.Id == fileId && d.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync();
    }

    public async Task<Dictionary<long, PassportFileDocument>> GetManyAsync(IReadOnlyCollection<long> fileIds,
        long ownerUserId)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<PassportFileDocument>.Filter.And(
            Builders<PassportFileDocument>.Filter.Eq(d => d.OwnerUserId, ownerUserId),
            Builders<PassportFileDocument>.Filter.In(d => d.Id, fileIds));

        var documents = await Collection.Find(filter).ToListAsync();

        return documents.ToDictionary(d => d.Id);
    }

    public async Task<(PassportFileDocument Document, byte[] Bytes)?> LoadRangeAsync(long fileId,
        long offset,
        int limit)
    {
        var document = await Collection.Find(d => d.Id == fileId).FirstOrDefaultAsync();
        if (document == null)
        {
            return null;
        }

        if (offset < 0)
        {
            offset = 0;
        }

        if (offset >= document.Size || limit <= 0)
        {
            return (document, []);
        }

        var length = (int)Math.Min(limit, document.Size - offset);
        var end = offset + length;

        // Read only the parts overlapping the requested window - a chunked download must not
        // re-materialise the whole blob on every request.
        var filter = Builders<PassportFilePartDocument>.Filter.And(
            Builders<PassportFilePartDocument>.Filter.Eq(p => p.FileId, fileId),
            Builders<PassportFilePartDocument>.Filter.Lt(p => p.Offset, end));

        var parts = await Parts.Find(filter)
            .Sort(Builders<PassportFilePartDocument>.Sort.Ascending(p => p.PartIndex))
            .ToListAsync();

        if (parts.Count == 0)
        {
            return null;
        }

        var result = new byte[length];
        foreach (var part in parts)
        {
            var partEnd = part.Offset + part.Bytes.Length;
            if (partEnd <= offset)
            {
                continue;
            }

            var copyFrom = (int)Math.Max(0, offset - part.Offset);
            var copyTo = (int)Math.Max(0, part.Offset - offset);
            var copyLength = (int)Math.Min(part.Bytes.Length - copyFrom, length - copyTo);
            if (copyLength <= 0)
            {
                continue;
            }

            Buffer.BlockCopy(part.Bytes, copyFrom, result, copyTo, copyLength);
        }

        return (document, result);
    }

    public async Task DeleteAsync(IReadOnlyCollection<long> fileIds, long ownerUserId)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var filter = Builders<PassportFileDocument>.Filter.And(
            Builders<PassportFileDocument>.Filter.Eq(d => d.OwnerUserId, ownerUserId),
            Builders<PassportFileDocument>.Filter.In(d => d.Id, fileIds));

        // Resolve first, so a caller that passed an id belonging to somebody else cannot delete that
        // user's parts.
        var owned = await Collection.Find(filter).Project(d => d.Id).ToListAsync();
        if (owned.Count == 0)
        {
            return;
        }

        await Collection.DeleteManyAsync(Builders<PassportFileDocument>.Filter.In(d => d.Id, owned));
        await Parts.DeleteManyAsync(Builders<PassportFilePartDocument>.Filter.In(p => p.FileId, owned));
    }

    public async Task DeleteAllAsync(long ownerUserId)
    {
        var owned = await Collection
            .Find(d => d.OwnerUserId == ownerUserId)
            .Project(d => d.Id)
            .ToListAsync();

        await DeleteAsync(owned, ownerUserId);
    }

    private async Task<List<BsonDocument>> LoadUploadedPartsAsync(long userId, long fileId)
    {
        var partsCollection = mongoDatabase.GetCollection<BsonDocument>(UploadPartsCollectionName);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("FileId", fileId));

        return await partsCollection.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .ToListAsync();
    }

    private static byte[] AssembleBlob(List<BsonDocument> parts)
    {
        var totalSize = parts.Sum(p => p["Bytes"].AsByteArray.Length);
        var blob = new byte[totalSize];
        var offset = 0;
        foreach (var part in parts)
        {
            var bytes = part["Bytes"].AsByteArray;
            Buffer.BlockCopy(bytes, 0, blob, offset, bytes.Length);
            offset += bytes.Length;
        }

        return blob;
    }

    private static string BuildPartId(long fileId, int partIndex)
    {
        return $"{fileId}_{partIndex}";
    }

    /// <summary>
    /// Generates a passport-file <c>id</c>/<c>access_hash</c> pair. Both are handed to clients, so they
    /// must come from a CSPRNG: <see cref="Random.Shared"/> is xoshiro256**, whose state is recoverable
    /// from a few observed outputs.
    /// </summary>
    private static long NewNonZeroId()
    {
        while (true)
        {
            var id = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            if (id != 0)
            {
                return id;
            }
        }
    }

    /// <summary>Creates the indexes once; a failed attempt is not cached (see EncryptedFileStore).</summary>
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

    private async Task CreateIndexesAsync()
    {
        await Parts.Indexes.CreateOneAsync(new CreateIndexModel<PassportFilePartDocument>(
            Builders<PassportFilePartDocument>.IndexKeys
                .Ascending(p => p.FileId)
                .Ascending(p => p.PartIndex)));

        await Collection.Indexes.CreateOneAsync(new CreateIndexModel<PassportFileDocument>(
            Builders<PassportFileDocument>.IndexKeys.Ascending(d => d.OwnerUserId)));

        // upload.saveFilePart writes plain BsonDocuments; the store path filters on (UserId, FileId).
        var uploadParts = mongoDatabase.GetCollection<BsonDocument>(UploadPartsCollectionName);
        await uploadParts.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("UserId").Ascending("FileId").Ascending("FilePart")));
    }
}
