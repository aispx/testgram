using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Upload notification sound, use <a href="https://corefork.telegram.org/method/account.saveRingtone">account.saveRingtone</a> to convert it and add it to the list of saved notification sounds.
/// Possible errors
/// Code Type Description
/// 400 RINGTONE_MIME_INVALID The MIME type for the ringtone is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.uploadRingtone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Supported formats: MP3, OGG OPUS
/// </remarks>
internal sealed class UploadRingtoneHandler(
    IMongoDatabase database,
    IFileReferenceHelper fileReferenceHelper,
    ILogger<UploadRingtoneHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUploadRingtone, MyTelegram.Schema.IDocument>
{
    protected override async Task<MyTelegram.Schema.IDocument> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUploadRingtone obj)
    {
        // Validate MIME type - only MP3 and OGG OPUS allowed
        var allowedMimeTypes = new[] { "audio/mpeg", "audio/mp3", "audio/ogg", "audio/opus" };
        if (!allowedMimeTypes.Contains(obj.MimeType?.ToLower()))
        {
            logger.LogWarning("Invalid ringtone MIME type: {MimeType}", obj.MimeType);
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();
        }

        // Validate file name
        if (string.IsNullOrWhiteSpace(obj.FileName))
        {
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();
        }

        // Extract file info from InputFile
        long fileId = 0;
        string fileName = obj.FileName;
        int fileSize = 0;

        if (obj.File is MyTelegram.Schema.TInputFile inputFile)
        {
            fileId = inputFile.Id;
            fileName = inputFile.Name;
            fileSize = inputFile.Parts * 512 * 1024; // Approximate size
        }
        else if (obj.File is MyTelegram.Schema.TInputFileBig inputFileBig)
        {
            fileId = inputFileBig.Id;
            fileName = inputFileBig.Name;
            fileSize = inputFileBig.Parts * 512 * 1024;
        }

        if (fileId == 0)
        {
            RpcErrors.RpcErrors400.RingtoneMimeInvalid.ThrowRpcError();
        }

        // Generate document ID
        var documentId = GenerateId();
        var accessHash = Random.Shared.NextInt64();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Store ringtone document in MongoDB
        var collection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var doc = new BsonDocument
        {
            ["_id"] = $"documentreadmodel-{documentId}",
            ["DocumentId"] = documentId,
            ["AccessHash"] = accessHash,
            ["Date"] = (int)now,
            ["MimeType"] = obj.MimeType,
            ["Size"] = fileSize,
            ["FileName"] = fileName,
            ["FileId"] = fileId,
            ["OwnerId"] = input.UserId,
            ["IsRingtone"] = true,
            ["Attributes"] = new BsonArray
            {
                new BsonDocument
                {
                    ["Type"] = "TDocumentAttributeFilename",
                    ["FileName"] = fileName
                },
                new BsonDocument
                {
                    ["Type"] = "TDocumentAttributeAudio",
                    ["Voice"] = false,
                    ["Duration"] = 5, // Default duration, will be updated after processing
                    ["Title"] = Path.GetFileNameWithoutExtension(fileName),
                    ["Performer"] = ""
                }
            }
        };

        await collection.InsertOneAsync(doc);

        logger.LogInformation("Ringtone uploaded: DocumentId={DocumentId}, FileName={FileName}, UserId={UserId}",
            documentId, fileName, input.UserId);

        // Return document
        return new MyTelegram.Schema.TDocument
        {
            Id = documentId,
            AccessHash = accessHash,
            // See https://corefork.telegram.org/api/file-references
            FileReference = fileReferenceHelper.Create(AccessHashType.Document, documentId),
            Date = (int)now,
            MimeType = obj.MimeType,
            Size = fileSize,
            Thumbs = new TVector<MyTelegram.Schema.IPhotoSize>(),
            VideoThumbs = new TVector<MyTelegram.Schema.IVideoSize>(),
            DcId = 1,
            Attributes = new TVector<MyTelegram.Schema.IDocumentAttribute>
            {
                new MyTelegram.Schema.TDocumentAttributeFilename
                {
                    FileName = fileName
                },
                new MyTelegram.Schema.TDocumentAttributeAudio
                {
                    Voice = false,
                    Duration = 5,
                    Title = Path.GetFileNameWithoutExtension(fileName),
                    Performer = ""
                }
            }
        };
    }

    private static long GenerateId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
    }
}
