using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.SecretChat;

namespace MyTelegram.Messenger.Messenger.Handlers.LatestLayer.Upload;

/// <summary>
/// Returns content of a whole file or its part.
/// See https://core.telegram.org/method/upload.getFile
/// </summary>
internal sealed class GetFileHandler : RpcResultObjectHandler<MyTelegram.Schema.Upload.RequestGetFile, MyTelegram.Schema.Upload.IFile>
{
    private readonly IMongoDatabase _database;
    private readonly ILogger<GetFileHandler> _logger;
    private readonly IHlsGroupCallStreamService _hlsGroupCallStreamService;
    private readonly IEncryptedFileStore _encryptedFileStore;

    public GetFileHandler(
        IMongoDatabase database,
        ILogger<GetFileHandler> logger,
        IHlsGroupCallStreamService hlsGroupCallStreamService,
        IEncryptedFileStore encryptedFileStore)
    {
        _database = database;
        _logger = logger;
        _hlsGroupCallStreamService = hlsGroupCallStreamService;
        _encryptedFileStore = encryptedFileStore;
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

        if (obj.Location is TInputGroupCallStream groupCallStream)
        {
            return await HandleGroupCallStreamAsync(input, groupCallStream, obj.Limit);
        }

        // Encrypted-file download for secret chats. Branch BEFORE the file_parts lookup below,
        // which filters by the caller's UserId and would reject the recipient. access_hash is the
        // capability token (as on Telegram proper) — no membership join.
        if (obj.Location is TInputEncryptedFileLocation encryptedFileLocation)
        {
            // Reads only the requested window, not the whole blob.
            var loaded = await _encryptedFileStore.LoadRangeAsync(encryptedFileLocation.Id,
                encryptedFileLocation.AccessHash,
                obj.Offset,
                obj.Limit);
            if (loaded == null)
            {
                RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
            }

            return new MyTelegram.Schema.Upload.TFile
            {
                Type = new MyTelegram.Schema.Storage.TFilePartial(),
                Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Bytes = loaded!.Value.Bytes
            };
        }

        // Extract file ID from location
        long fileId = obj.Location switch
        {
            TInputDocumentFileLocation doc => doc.Id,
            TInputPhotoFileLocation photo => photo.Id,
            TInputFileLocation file => file.VolumeId, // Legacy
            TInputPeerPhotoFileLocation peerPhoto => peerPhoto.PhotoId,
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

        // File not found in parts, check if it's a stored document/photo
        // Stored files should be served by the FileServer, not the messenger server
        var documentsCollection = _database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var docFilter = Builders<BsonDocument>.Filter.Eq("DocumentId", fileId);
        var document = await documentsCollection.Find(docFilter).FirstOrDefaultAsync();

        if (document == null)
        {
            // Check if it's a photo
            var photosCollection = _database.GetCollection<BsonDocument>("eventflow-photoreadmodel");
            var photoFilter = Builders<BsonDocument>.Filter.Eq("PhotoId", fileId);
            var photo = await photosCollection.Find(photoFilter).FirstOrDefaultAsync();

            if (photo == null)
            {
                RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
            }
        }

        // Stored files are served by the FileServer; messenger cannot serve them directly
        _logger.LogWarning("GetFile called for stored file {FileId} - returning FileIdInvalid (should be served by FileServer)", fileId);
        RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();

        // Unreachable
        throw new InvalidOperationException();
    }

    private async Task<MyTelegram.Schema.Upload.IFile> HandleGroupCallStreamAsync(
        IRequestInput input,
        TInputGroupCallStream location,
        int limit)
    {
        if (location.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return null!;
        }

        var groupCalls = _database.GetCollection<GroupCallDocument>("group_calls");
        var groupCall = await groupCalls.Find(GroupCallStateHelper.Filter(inputGroupCall)).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return null!;
        }

        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        _logger.LogInformation(
            "Handling upload.getFile inputGroupCallStream: ReqMsgId={ReqMsgId}, CallId={CallId}, TimeMs={TimeMs}, Scale={Scale}, VideoChannel={VideoChannel}, VideoQuality={VideoQuality}",
            input.ReqMsgId,
            groupCall.CallId,
            location.TimeMs,
            location.Scale,
            location.VideoChannel,
            location.VideoQuality);

        var bytes = await _hlsGroupCallStreamService.ReadPartAsync(groupCall, location);
        if (bytes.Length > limit)
        {
            bytes = bytes.Take(limit).ToArray();
        }

        _logger.LogInformation(
            "Returning upload.getFile inputGroupCallStream: ReqMsgId={ReqMsgId}, CallId={CallId}, Bytes={Bytes}",
            input.ReqMsgId,
            groupCall.CallId,
            bytes.Length);

        return new MyTelegram.Schema.Upload.TFile
        {
            Type = new MyTelegram.Schema.Storage.TFilePartial(),
            Mtime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Bytes = bytes
        };
    }
}
