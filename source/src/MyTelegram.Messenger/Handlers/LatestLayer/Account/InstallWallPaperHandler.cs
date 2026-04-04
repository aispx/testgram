using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Install wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.installWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InstallWallPaperHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestInstallWallPaper, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestInstallWallPaper obj)
    {
        long wallpaperId = 0;

        // Extract wallpaper ID
        if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaper inputWallpaper)
        {
            wallpaperId = inputWallpaper.Id;
        }
        else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperSlug inputSlug)
        {
            var wallpaperCol = database.GetCollection<BsonDocument>("wallpapers");
            var slugFilter = Builders<BsonDocument>.Filter.Eq("Slug", inputSlug.Slug);
            var doc = await wallpaperCol.Find(slugFilter).FirstOrDefaultAsync();
            
            if (doc == null)
            {
                RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
            }
            
            wallpaperId = doc["WallpaperId"].AsInt64;
        }
        else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperNoFile inputNoFile)
        {
            wallpaperId = inputNoFile.Id;
        }

        if (wallpaperId == 0)
        {
            RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
        }

        // Set as installed wallpaper
        var collection = database.GetCollection<BsonDocument>("user_settings");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var update = Builders<BsonDocument>.Update
            .Set("InstalledWallpaperId", wallpaperId)
            .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await collection.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true }
        );

        // Also save to user_wallpapers if not already saved
        var savedCol = database.GetCollection<BsonDocument>("user_wallpapers");
        var savedFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("WallpaperId", wallpaperId)
        );
        
        var exists = await savedCol.Find(savedFilter).AnyAsync();
        if (!exists)
        {
            await savedCol.InsertOneAsync(new BsonDocument
            {
                ["UserId"] = input.UserId,
                ["WallpaperId"] = wallpaperId,
                ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        return new TBoolTrue();
    }
}
