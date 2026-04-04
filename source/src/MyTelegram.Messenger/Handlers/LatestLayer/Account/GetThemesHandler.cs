using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get installed themes
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getThemes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetThemesHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetThemes, MyTelegram.Schema.Account.IThemes>
{
    protected override async Task<MyTelegram.Schema.Account.IThemes> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetThemes obj)
    {
        // Get user's installed themes
        var collection = database.GetCollection<BsonDocument>("user_themes");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var userThemeDocs = await collection.Find(filter).ToListAsync();

        if (userThemeDocs.Count == 0)
        {
            return new MyTelegram.Schema.Account.TThemes
            {
                Hash = obj.Hash,
                Themes = new TVector<MyTelegram.Schema.ITheme>()
            };
        }

        var themeIds = userThemeDocs.Select(d => d["ThemeId"].AsInt64).ToList();
        
        // Load theme details
        var themesCol = database.GetCollection<BsonDocument>("themes");
        var themesFilter = Builders<BsonDocument>.Filter.In("ThemeId", themeIds);
        var themeDocs = await themesCol.Find(themesFilter).ToListAsync();

        var themes = new TVector<MyTelegram.Schema.ITheme>();
        
        foreach (var doc in themeDocs)
        {
            themes.Add(new MyTelegram.Schema.TTheme
            {
                Id = doc["ThemeId"].AsInt64,
                AccessHash = doc["AccessHash"].AsInt64,
                Slug = doc["Slug"].AsString,
                Title = doc["Title"].AsString,
                Creator = doc.Contains("CreatorUserId") && doc["CreatorUserId"].AsInt64 == input.UserId,
                Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
                Settings = new TVector<MyTelegram.Schema.IThemeSettings>()
            });
        }

        return new MyTelegram.Schema.Account.TThemes
        {
            Hash = obj.Hash,
            Themes = themes
        };
    }
}
