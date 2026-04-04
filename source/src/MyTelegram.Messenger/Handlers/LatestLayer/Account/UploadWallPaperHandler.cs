using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Create and upload a new <a href="https://corefork.telegram.org/api/wallpapers">wallpaper</a>
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_FILE_INVALID The specified wallpaper file is invalid.
/// 400 WALLPAPER_MIME_INVALID The specified wallpaper MIME type is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.uploadWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UploadWallPaperHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUploadWallPaper, MyTelegram.Schema.IWallPaper>
{
    protected override async Task<MyTelegram.Schema.IWallPaper> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUploadWallPaper obj)
    {
        // Validate MIME type
        var validMimeTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/webp" };
        if (!validMimeTypes.Contains(obj.MimeType.ToLower()))
        {
            RpcErrors.RpcErrors400.WallpaperMimeInvalid.ThrowRpcError();
        }

        // Get uploaded file info
        MyTelegram.Schema.TInputFile? inputFile = null;
        if (obj.File is MyTelegram.Schema.TInputFile file)
        {
            inputFile = file;
        }

        if (inputFile == null)
        {
            RpcErrors.RpcErrors400.WallpaperFileInvalid.ThrowRpcError();
        }

        // Generate IDs
        var wallpaperId = GenerateId();
        var accessHash = GenerateAccessHash();
        var documentId = inputFile.Id;

        // Create slug from file name
        var slug = $"custom_{wallpaperId}";

        // Insert into wallpapers collection
        var collection = database.GetCollection<BsonDocument>("wallpapers");
        await collection.InsertOneAsync(new BsonDocument
        {
            ["WallpaperId"] = wallpaperId,
            ["AccessHash"] = accessHash,
            ["Slug"] = slug,
            ["DocumentId"] = documentId,
            ["Creator"] = true,
            ["Default"] = false,
            ["Pattern"] = false,
            ["Dark"] = false,
            ["CreatedBy"] = input.UserId,
            ["CreatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        // Auto-save for uploader
        var userWallpaperCol = database.GetCollection<BsonDocument>("user_wallpapers");
        await userWallpaperCol.InsertOneAsync(new BsonDocument
        {
            ["UserId"] = input.UserId,
            ["WallpaperId"] = wallpaperId,
            ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        // Return wallpaper
        return new MyTelegram.Schema.TWallPaper
        {
            Id = wallpaperId,
            AccessHash = accessHash,
            Slug = slug,
            Creator = true,
            Default = false,
            Pattern = false,
            Dark = false,
            Document = new MyTelegram.Schema.TDocumentEmpty { Id = documentId },
            Settings = obj.Settings
        };
    }

    private static long GenerateId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
    }

    private static long GenerateAccessHash()
    {
        return Random.Shared.NextInt64();
    }
}
