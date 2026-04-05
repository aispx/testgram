using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Saves a part of file for further sending to one of the methods.
/// See https://core.telegram.org/method/upload.saveFilePart
/// </summary>
internal sealed class SaveFilePartHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestSaveFilePart, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<SaveFilePartHandler> _logger;

    public SaveFilePartHandler(IMongoDatabase database, ILogger<SaveFilePartHandler> logger)
    {
        _database = database;
        _logger = logger;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Upload.RequestSaveFilePart obj)
    {
        // Validate input
        if (obj.Bytes.IsEmpty)
        {
            RpcErrors.RpcErrors400.FilePartEmpty.ThrowRpcError();
        }

        if (obj.FilePart < 0)
        {
            RpcErrors.RpcErrors400.FilePartInvalid.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("file_parts");

        // Create file part document
        var filePartDoc = new BsonDocument
        {
            ["_id"] = $"{input.UserId}_{obj.FileId}_{obj.FilePart}",
            ["UserId"] = input.UserId,
            ["FileId"] = obj.FileId,
            ["FilePart"] = obj.FilePart,
            ["Bytes"] = obj.Bytes.ToArray(),
            ["Size"] = obj.Bytes.Length,
            ["UploadedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Upsert file part (replace if exists)
        var filter = Builders<BsonDocument>.Filter.Eq("_id", filePartDoc["_id"]);
        await collection.ReplaceOneAsync(filter, filePartDoc, new ReplaceOptions { IsUpsert = true });

        _logger.LogDebug("Saved file part: UserId={UserId}, FileId={FileId}, Part={Part}, Size={Size}",
            input.UserId, obj.FileId, obj.FilePart, obj.Bytes.Length);

        return new TBoolTrue();
    }
}
