using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Get SHA256 hashes for verifying downloaded files
/// See https://core.telegram.org/method/upload.getFileHashes
/// </summary>
internal sealed class GetFileHashesHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetFileHashes, TVector<MyTelegram.Schema.IFileHash>>
{
    private readonly IMongoDatabase _database;

    public GetFileHashesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<TVector<MyTelegram.Schema.IFileHash>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetFileHashes obj)
    {
        // Extract file ID from location
        long fileId = obj.Location switch
        {
            TInputDocumentFileLocation doc => doc.Id,
            TInputPhotoFileLocation photo => photo.Id,
            TInputFileLocation file => file.VolumeId,
            _ => throw RpcErrors.RpcErrors400.LocationInvalid.ToRpcException()
        };

        var hashes = new TVector<MyTelegram.Schema.IFileHash>();

        // Get file parts from MongoDB
        var partsCollection = _database.GetCollection<BsonDocument>("file_parts");
        var partsFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FileId", fileId)
        );
        var parts = await partsCollection.Find(partsFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .ToListAsync();

        if (parts.Count == 0)
        {
            // No parts found, return empty hashes
            return hashes;
        }

        // Calculate SHA256 hash for each part
        long currentOffset = 0;
        foreach (var part in parts)
        {
            var partBytes = part["Bytes"].AsByteArray;
            var hash = SHA256.HashData(partBytes);

            hashes.Add(new TFileHash
            {
                Offset = currentOffset,
                Limit = partBytes.Length,
                Hash = hash
            });

            currentOffset += partBytes.Length;
        }

        return hashes;
    }
}
