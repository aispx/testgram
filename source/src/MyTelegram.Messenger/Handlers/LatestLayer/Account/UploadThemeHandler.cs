using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Upload theme
/// Possible errors
/// Code Type Description
/// 400 THEME_FILE_INVALID Invalid theme file provided.
/// 400 THEME_MIME_INVALID The theme's MIME type is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.uploadTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 222: account.uploadTheme#1c3db333 flags:# file:InputFile thumb:flags.0?InputFile file_name:string mime_type:string = Document;
/// </remarks>
internal sealed class UploadThemeHandler(
    IMongoDatabase database,
    ILogger<UploadThemeHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUploadTheme, MyTelegram.Schema.IDocument>
{
    protected override async Task<MyTelegram.Schema.IDocument> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUploadTheme obj)
    {
        // Validate MIME type
        var allowedMimeTypes = new[] { "application/x-tgtheme-android", "application/x-tgtheme-ios", "application/x-tgtheme-macos", "application/x-tgtheme-tdesktop" };
        if (!allowedMimeTypes.Contains(obj.MimeType))
        {
            logger.LogWarning("Invalid theme MIME type: {MimeType}", obj.MimeType);
            RpcErrors.RpcErrors400.ThemeMimeInvalid.ThrowRpcError();
        }

        // Validate file name
        if (string.IsNullOrWhiteSpace(obj.FileName) || !obj.FileName.EndsWith(".tgtheme", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Invalid theme file name: {FileName}", obj.FileName);
            RpcErrors.RpcErrors400.ThemeFileInvalid.ThrowRpcError();
        }

        // Extract file info from InputFile
        long fileId = 0;
        string? fileName = obj.FileName;
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
            RpcErrors.RpcErrors400.ThemeFileInvalid.ThrowRpcError();
        }

        // Generate document ID
        var documentId = GenerateId();
        var accessHash = Random.Shared.NextInt64();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Extract thumb info if provided
        long thumbPhotoId = 0;
        if (obj.Thumb != null)
        {
            if (obj.Thumb is MyTelegram.Schema.TInputFile thumbFile)
            {
                thumbPhotoId = thumbFile.Id;
            }
            else if (obj.Thumb is MyTelegram.Schema.TInputFileBig thumbFileBig)
            {
                thumbPhotoId = thumbFileBig.Id;
            }
        }

        // Store theme document in MongoDB
        var collection = database.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var doc = new BsonDocument
        {
            ["_id"] = $"documentreadmodel-{documentId}",
            ["DocumentId"] = documentId,
            ["AccessHash"] = accessHash,
            ["FileReference"] = Array.Empty<byte>(),
            ["Date"] = (int)now,
            ["MimeType"] = obj.MimeType,
            ["Size"] = fileSize,
            ["FileName"] = fileName,
            ["FileId"] = fileId,
            ["OwnerId"] = input.UserId,
            ["Attributes"] = new BsonArray
            {
                new BsonDocument
                {
                    ["Type"] = "TDocumentAttributeFilename",
                    ["FileName"] = fileName
                }
            }
        };

        if (thumbPhotoId > 0)
        {
            doc["ThumbPhotoId"] = thumbPhotoId;
        }

        await collection.InsertOneAsync(doc);

        logger.LogInformation("Theme uploaded: DocumentId={DocumentId}, FileName={FileName}, UserId={UserId}",
            documentId, fileName, input.UserId);

        // Return document
        return new MyTelegram.Schema.TDocument
        {
            Id = documentId,
            AccessHash = accessHash,
            FileReference = Array.Empty<byte>(),
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
                }
            }
        };
    }

    private static long GenerateId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
    }
}
