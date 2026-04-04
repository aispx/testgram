using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Install/uninstall wallpaper
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveWallPaper"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveWallPaperHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveWallPaper, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSaveWallPaper obj)
    {
        long wallpaperId = 0;

        // Extract wallpaper ID
        if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaper inputWallpaper)
        {
            wallpaperId = inputWallpaper.Id;
        }
        else if (obj.Wallpaper is MyTelegram.Schema.TInputWallPaperSlug inputSlug)
        {
            // Find by slug
            var wallpaperCol = database.GetCollection<BsonDocument>("wallpapers");
            var filter = Builders<BsonDocument>.Filter.Eq("Slug", inputSlug.Slug);
            var doc = await wallpaperCol.Find(filter).FirstOrDefaultAsync();
            
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

        var collection = database.GetCollection<BsonDocument>("user_wallpapers");

        if (obj.Unsave)
        {
            // Remove from saved list
            await collection.DeleteOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("WallpaperId", wallpaperId)
                )
            );
        }
        else
        {
            // Add to saved list
            var doc = new BsonDocument
            {
                ["UserId"] = input.UserId,
                ["WallpaperId"] = wallpaperId,
                ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await collection.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("WallpaperId", wallpaperId)
                ),
                doc,
                new ReplaceOptions { IsUpsert = true }
            );
        }

        return new TBoolTrue();
    }
}
