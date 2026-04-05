using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Saves a part of a large file (over 10 MB in size) to be later passed to one of the methods.
/// See https://core.telegram.org/method/upload.saveBigFilePart
/// </summary>
internal sealed class SaveBigFilePartHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestSaveBigFilePart, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<SaveBigFilePartHandler> _logger;

    public SaveBigFilePartHandler(IMongoDatabase database, ILogger<SaveBigFilePartHandler> logger)
    {
        _database = database;
        _logger = logger;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestSaveBigFilePart obj)
    {
        // Validate input
        if (obj.Bytes.IsEmpty)
        {
            RpcErrors.RpcErrors400.FilePartEmpty.ThrowRpcError();
        }

        if (obj.FilePart < 0 || obj.FilePart >= obj.FileTotalParts)
        {
            RpcErrors.RpcErrors400.FilePartInvalid.ThrowRpcError();
        }

        if (obj.FileTotalParts <= 0)
        {
            RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
        }

        // Validate part size (must be consistent except last part)
        const int minPartSize = 128 * 1024; // 128 KB
        const int maxPartSize = 512 * 1024; // 512 KB

        if (obj.Bytes.Length < minPartSize && obj.FilePart < obj.FileTotalParts - 1)
        {
            RpcErrors.RpcErrors400.FilePartTooSmall.ThrowRpcError();
        }

        if (obj.Bytes.Length > maxPartSize)
        {
            RpcErrors.RpcErrors400.FilePartTooBig.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("file_parts");

        // Check if part size changed (all parts except last must have same size)
        if (obj.FilePart > 0)
        {
            var firstPartFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("FileId", obj.FileId),
                Builders<BsonDocument>.Filter.Eq("FilePart", 0)
            );
            var firstPart = await collection.Find(firstPartFilter).FirstOrDefaultAsync();

            if (firstPart != null && obj.FilePart < obj.FileTotalParts - 1)
            {
                var firstPartSize = firstPart["Size"].AsInt32;
                if (obj.Bytes.Length != firstPartSize)
                {
                    RpcErrors.RpcErrors400.FilePartSizeChanged.ThrowRpcError();
                }
            }
        }

        // Create file part document
        var filePartDoc = new BsonDocument
        {
            ["_id"] = $"{input.UserId}_{obj.FileId}_{obj.FilePart}",
            ["UserId"] = input.UserId,
            ["FileId"] = obj.FileId,
            ["FilePart"] = obj.FilePart,
            ["FileTotalParts"] = obj.FileTotalParts,
            ["Bytes"] = obj.Bytes.ToArray(),
            ["Size"] = obj.Bytes.Length,
            ["IsBigFile"] = true,
            ["UploadedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Upsert file part
        var filter = Builders<BsonDocument>.Filter.Eq("_id", filePartDoc["_id"]);
        await collection.ReplaceOneAsync(filter, filePartDoc, new ReplaceOptions { IsUpsert = true });

        _logger.LogDebug("Saved big file part: UserId={UserId}, FileId={FileId}, Part={Part}/{Total}, Size={Size}",
            input.UserId, obj.FileId, obj.FilePart, obj.FileTotalParts, obj.Bytes.Length);

        return new TBoolTrue();
    }
}
