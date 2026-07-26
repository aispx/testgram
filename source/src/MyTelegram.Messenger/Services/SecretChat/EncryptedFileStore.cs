using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.SecretChat;

/// <summary>
/// Snapshot of one part of a stored encrypted file. The upload staging collection (<c>file_parts</c>)
/// is keyed by the CLIENT-chosen file id and is upserted, so a client reusing a file id would rewrite
/// the bytes of an already-sent file. Parts are therefore copied into this immutable collection,
/// keyed by the SERVER-assigned file id, at store time.
/// </summary>
public class EncryptedFilePartDocument
{
    /// <summary>"{FileId}_{PartIndex}"</summary>
    public string Id { get; set; } = null!;

    public long FileId { get; set; }

    public int PartIndex { get; set; }

    /// <summary>Byte offset of this part within the assembled blob.</summary>
    public long Offset { get; set; }

    public byte[] Bytes { get; set; } = [];
}

public class EncryptedFileStore(
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options) : IEncryptedFileStore, ISingletonDependency
{
    private const string CollectionName = "encrypted_files";
    private const string PartsCollectionName = "encrypted_file_parts";
    private const string UploadPartsCollectionName = "file_parts";

    private readonly Lock _indexInitLock = new();
    private Task? _indexInit;

    private IMongoCollection<EncryptedFileDocument> Collection =>
        mongoDatabase.GetCollection<EncryptedFileDocument>(CollectionName);

    private IMongoCollection<EncryptedFilePartDocument> Parts =>
        mongoDatabase.GetCollection<EncryptedFilePartDocument>(PartsCollectionName);

    public async Task<EncryptedFileDescriptor> StoreUploadedAsync(long userId,
        long clientFileId,
        int declaredParts,
        int keyFingerprint,
        string? md5Checksum)
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
        // concatenated away and the recipient would get a blob that cannot be decrypted.
        for (var index = 0; index < uploadedParts.Count; index++)
        {
            if (uploadedParts[index]["FilePart"].ToInt32() != index)
            {
                RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
            }
        }

        var blob = AssembleBlob(uploadedParts);

        if (!string.IsNullOrEmpty(md5Checksum))
        {
            var actualMd5 = Convert.ToHexString(MD5.HashData(blob)).ToLowerInvariant();
            if (!string.Equals(actualMd5, md5Checksum, StringComparison.OrdinalIgnoreCase))
            {
                RpcErrors.RpcErrors400.Md5ChecksumInvalid.ThrowRpcError();
            }
        }

        var document = new EncryptedFileDocument
        {
            Id = NewNonZeroId(),
            AccessHash = NewNonZeroId(),
            Size = blob.LongLength,
            DcId = options.CurrentValue.ThisDcId > 0 ? options.CurrentValue.ThisDcId : 1,
            KeyFingerprint = keyFingerprint,
            OwnerUserId = userId,
            SourceFileId = clientFileId,
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

        // Immutable snapshot: the client may reuse clientFileId for a different file afterwards.
        var offset = 0L;
        var snapshots = new List<EncryptedFilePartDocument>(uploadedParts.Count);
        for (var index = 0; index < uploadedParts.Count; index++)
        {
            var bytes = uploadedParts[index]["Bytes"].AsByteArray;
            snapshots.Add(new EncryptedFilePartDocument
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

        return ToDescriptor(document);
    }

    public async Task<EncryptedFileDescriptor?> ResolveAsync(long fileId, long accessHash)
    {
        var document = await Collection
            .Find(d => d.Id == fileId && d.AccessHash == accessHash)
            .FirstOrDefaultAsync();

        return document == null ? null : ToDescriptor(document);
    }

    public async Task<(EncryptedFileDocument Document, byte[] Blob)?> LoadForDownloadAsync(long fileId,
        long accessHash)
    {
        var range = await LoadRangeAsync(fileId, accessHash, offset: 0, limit: int.MaxValue);

        return range == null ? null : (range.Value.Document, range.Value.Bytes);
    }

    public async Task<(EncryptedFileDocument Document, byte[] Bytes)?> LoadRangeAsync(long fileId,
        long accessHash,
        long offset,
        int limit)
    {
        // access_hash is the capability token (as on Telegram proper) — no membership join.
        var document = await Collection
            .Find(d => d.Id == fileId && d.AccessHash == accessHash)
            .FirstOrDefaultAsync();

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

        // Read only the parts overlapping the requested window — a chunked download of a large file
        // must not re-materialise the whole blob on every request.
        var filter = Builders<EncryptedFilePartDocument>.Filter.And(
            Builders<EncryptedFilePartDocument>.Filter.Eq(p => p.FileId, fileId),
            Builders<EncryptedFilePartDocument>.Filter.Lt(p => p.Offset, end));

        var parts = await Parts.Find(filter)
            .Sort(Builders<EncryptedFilePartDocument>.Sort.Ascending(p => p.PartIndex))
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

    private static long NewNonZeroId()
    {
        var id = Math.Abs(Random.Shared.NextInt64());

        return id == 0 ? 1 : id;
    }

    private static EncryptedFileDescriptor ToDescriptor(EncryptedFileDocument document)
    {
        return new EncryptedFileDescriptor(document.Id,
            document.AccessHash,
            document.Size,
            document.DcId,
            document.KeyFingerprint);
    }

    /// <summary>Creates the indexes once; a failed attempt is not cached (see SecretChatMessageStore).</summary>
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
        await Parts.Indexes.CreateOneAsync(new CreateIndexModel<EncryptedFilePartDocument>(
            Builders<EncryptedFilePartDocument>.IndexKeys
                .Ascending(p => p.FileId)
                .Ascending(p => p.PartIndex)));

        // upload.saveFilePart writes plain BsonDocuments; the download path filters on (UserId, FileId).
        var uploadParts = mongoDatabase.GetCollection<BsonDocument>(UploadPartsCollectionName);
        await uploadParts.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("UserId").Ascending("FileId").Ascending("FilePart")));
    }
}
