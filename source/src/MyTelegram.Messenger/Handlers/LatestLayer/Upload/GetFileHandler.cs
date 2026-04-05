using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Returns content of a whole file or its part.
/// See https://core.telegram.org/method/upload.getFile
/// </summary>
internal sealed class GetFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetFile, MyTelegram.Schema.Upload.IFile>
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<GetFileHandler> _logger;

    public GetFileHandler(IMongoDatabase database, ILogger<GetFileHandler> logger)
    {
        _database = database;
        _logger = logger;
    }

    protected override async Task<MyTelegram.Schema.Upload.IFile> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestGetFile obj)
    {
        // Validate input
        if (obj.Limit <= 0 || obj.Limit > 1024 * 1024) // Max 1MB per request
        {
            RpcErrors.RpcErrors400.LimitInvalid.ThrowRpcError();
        }

        if (obj.Offset < 0)
        {
            RpcErrors.RpcErrors400.OffsetInvalid.ThrowRpcError();
        }

        // Extract file ID from location
        long fileId = obj.Location switch
        {
            TInputDocumentFileLocation doc => doc.Id,
            TInputPhotoFileLocation photo => photo.Id,
            TInputFileLocation file => file.VolumeId, // Legacy
            _ => 0
        };

        if (fileId == 0)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
        }

        // Try to get file from uploaded parts first (for recently uploaded files)
        var partsCollection = _database.GetCollection<BsonDocument>("file_parts");
        var partsFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FileId", fileId)
        );
        var parts = await partsCollection.Find(partsFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("FilePart"))
            .ToListAsync();

        if (parts.Count > 0)
        {
            // Assemble file from parts
            var allBytes = new List<byte>();
            foreach (var part in parts)
            {
                var partBytes = part["Bytes"].AsByteArray;
                allBytes.AddRange(partBytes);
            }

            var fileBytes = allBytes.ToArray();

            // Apply offset and limit
            var start = (int)Math.Min(obj.Offset, fileBytes.Length);
            var length = Math.Min(obj.Limit, fileBytes.Length - start);
            var resultBytes = new byte[length];
            Array.Copy(fileBytes, start, resultBytes, 0, length);

            _logger.LogDebug("Retrieved file from parts: FileId={FileId}, Offset={Offset}, Limit={Limit}, Returned={Length}",
                fileId, obj.Offset, obj.Limit, resultBytes.Length);

            return new MyTelegram.Schema.Upload.TFile
            {
                Type = new MyTelegram.Schema.Storage.TFilePartial(), // Partial file type
                Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bytes = resultBytes
            };
        }

        // File not found in parts, check if it's a stored document
        var documentsCollection = _database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", fileId);
        var document = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        if (document == null)
        {
            RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
        }

        // For stored documents, return empty bytes (actual file data should be in FileServer/MinIO)
        // This is a simplified implementation - full implementation would fetch from FileServer
        _logger.LogWarning("GetFile called for stored document {FileId} - returning empty (FileServer integration needed)", fileId);

        return new MyTelegram.Schema.Upload.TFile
        {
            Type = new MyTelegram.Schema.Storage.TFileUnknown(), // Unknown type for stored files
            Mtime = document.Contains("Date") ? document["Date"].AsInt32 : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = Array.Empty<byte>() // Empty - needs FileServer integration
        };
    }
}
