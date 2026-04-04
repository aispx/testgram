using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Install a theme
/// <para><c>See <a href="https://corefork.telegram.org/method/account.installTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InstallThemeHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestInstallTheme, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestInstallTheme obj)
    {
        long themeId = 0;

        // Extract theme ID
        if (obj.Theme is MyTelegram.Schema.TInputTheme inputTheme)
        {
            themeId = inputTheme.Id;
        }
        else if (obj.Theme is MyTelegram.Schema.TInputThemeSlug inputSlug)
        {
            var themesCol = database.GetCollection<BsonDocument>("themes");
            var slugFilter = Builders<BsonDocument>.Filter.Eq("Slug", inputSlug.Slug);
            var doc = await themesCol.Find(slugFilter).FirstOrDefaultAsync();
            
            if (doc == null)
            {
                RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
            }
            
            themeId = doc["ThemeId"].AsInt64;
        }

        if (themeId == 0)
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
        }

        // Set as installed theme
        var collection = database.GetCollection<BsonDocument>("user_settings");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var update = Builders<BsonDocument>.Update
            .Set("InstalledThemeId", themeId)
            .Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await collection.UpdateOneAsync(
            filter,
            update,
            new UpdateOptions { IsUpsert = true }
        );

        // Also save to user_themes if not already saved
        var savedCol = database.GetCollection<BsonDocument>("user_themes");
        var savedFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("ThemeId", themeId)
        );
        
        var exists = await savedCol.Find(savedFilter).AnyAsync();
        if (!exists)
        {
            await savedCol.InsertOneAsync(new BsonDocument
            {
                ["UserId"] = input.UserId,
                ["ThemeId"] = themeId,
                ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        return new TBoolTrue();
    }
}
