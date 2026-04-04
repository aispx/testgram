using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get theme information
/// Possible errors
/// Code Type Description
/// 400 THEME_FORMAT_INVALID Invalid theme format provided.
/// 400 THEME_INVALID Invalid theme provided.
/// 400 THEME_SLUG_INVALID The specified theme slug is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetThemeHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetTheme, MyTelegram.Schema.ITheme>
{
    protected override async Task<MyTelegram.Schema.ITheme> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetTheme obj)
    {
        long themeId = 0;
        string? slug = null;

        if (obj.Theme is MyTelegram.Schema.TInputTheme inputTheme)
        {
            themeId = inputTheme.Id;
        }
        else if (obj.Theme is MyTelegram.Schema.TInputThemeSlug inputSlug)
        {
            slug = inputSlug.Slug;
        }

        var collection = database.GetCollection<BsonDocument>("themes");
        FilterDefinition<BsonDocument> filter;

        if (!string.IsNullOrEmpty(slug))
        {
            filter = Builders<BsonDocument>.Filter.Eq("Slug", slug);
        }
        else if (themeId != 0)
        {
            filter = Builders<BsonDocument>.Filter.Eq("ThemeId", themeId);
        }
        else
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
            return null!;
        }

        var doc = await collection.Find(filter).FirstOrDefaultAsync();
        if (doc == null)
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
        }

        return new MyTelegram.Schema.TTheme
        {
            Id = doc["ThemeId"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            Slug = doc["Slug"].AsString,
            Title = doc["Title"].AsString,
            Creator = doc.Contains("CreatorUserId") && doc["CreatorUserId"].AsInt64 == input.UserId,
            Default = doc.Contains("IsDefault") && doc["IsDefault"].AsBoolean,
            Settings = new TVector<MyTelegram.Schema.IThemeSettings>()
        };
    }
}
