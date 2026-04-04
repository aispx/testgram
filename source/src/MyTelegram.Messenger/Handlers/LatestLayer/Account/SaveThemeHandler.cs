using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Save a theme
/// Possible errors
/// Code Type Description
/// 400 THEME_INVALID Invalid theme provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveThemeHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveTheme, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSaveTheme obj)
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
            var filter = Builders<BsonDocument>.Filter.Eq("Slug", inputSlug.Slug);
            var doc = await themesCol.Find(filter).FirstOrDefaultAsync();
            
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

        var collection = database.GetCollection<BsonDocument>("user_themes");

        if (obj.Unsave)
        {
            // Remove from saved list
            await collection.DeleteOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("ThemeId", themeId)
                )
            );
        }
        else
        {
            // Add to saved list
            var doc = new BsonDocument
            {
                ["UserId"] = input.UserId,
                ["ThemeId"] = themeId,
                ["SavedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await collection.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("ThemeId", themeId)
                ),
                doc,
                new ReplaceOptions { IsUpsert = true }
            );
        }

        return new TBoolTrue();
    }
}
