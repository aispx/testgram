using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Localization;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// Reads the client-reported <c>lang_code</c> from the device read model. The most recently active
/// device wins, so a user who switched their app to another language gets server texts in that
/// language on the next message.
/// </summary>
public class UserLanguageResolver(IMongoDatabase database)
    : IUserLanguageResolver, ITransientDependency
{
    private IMongoCollection<BsonDocument> Devices =>
        database.GetCollection<BsonDocument>("eventflow-devicereadmodel");

    public async Task<string> GetLanguageAsync(long userId)
    {
        if (userId <= 0)
        {
            return ServerLanguage.Default;
        }

        var doc = await Devices
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .Sort(Builders<BsonDocument>.Sort.Descending("DateActive"))
            .Project(LanguageProjection)
            .FirstOrDefaultAsync();

        return doc == null ? ServerLanguage.Default : ServerLanguage.Normalize(ReadLangCode(doc));
    }

    public async Task<Dictionary<long, string>> GetLanguagesAsync(IReadOnlyCollection<long> userIds)
    {
        var result = userIds.Distinct().ToDictionary(p => p, _ => ServerLanguage.Default);
        if (result.Count == 0)
        {
            return result;
        }

        // One query for all recipients, then keep the newest device per user.
        var docs = await Devices
            .Find(Builders<BsonDocument>.Filter.In("UserId", result.Keys))
            .Sort(Builders<BsonDocument>.Sort.Descending("DateActive"))
            .Project(LanguageProjection)
            .ToListAsync();

        var seen = new HashSet<long>();
        foreach (var doc in docs)
        {
            if (!doc.TryGetValue("UserId", out var userIdValue) || userIdValue.IsBsonNull)
            {
                continue;
            }

            var userId = userIdValue.ToInt64();
            if (!seen.Add(userId) || !result.ContainsKey(userId))
            {
                continue;
            }

            result[userId] = ServerLanguage.Normalize(ReadLangCode(doc));
        }

        return result;
    }

    /// <summary>
    /// <c>LangCode</c> is the app language; <c>SystemLangCode</c> is the OS one and is used only
    /// when the app did not report a language of its own.
    /// </summary>
    private static string? ReadLangCode(BsonDocument doc)
    {
        var langCode = ReadString(doc, "LangCode");
        return string.IsNullOrWhiteSpace(langCode) ? ReadString(doc, "SystemLangCode") : langCode;
    }

    private static string? ReadString(BsonDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && value.IsString ? value.AsString : null;

    private static ProjectionDefinition<BsonDocument> LanguageProjection =>
        Builders<BsonDocument>.Projection
            .Include("UserId")
            .Include("LangCode")
            .Include("SystemLangCode");
}
