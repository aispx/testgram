using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get localized <a href="https://corefork.telegram.org/api/custom-emoji#emoji-keywords">emoji keywords »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiKeywords"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetEmojiKeywordsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiKeywords, MyTelegram.Schema.IEmojiKeywordsDifference>
{
    protected override async Task<MyTelegram.Schema.IEmojiKeywordsDifference> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiKeywords obj)
    {
        var langCode = NormalizeLangCode(obj.LangCode);
        var collection = mongoDatabase.GetCollection<BsonDocument>("emoji_keywords");
        var filter = Builders<BsonDocument>.Filter.Eq("LangCode", langCode);
        var docs = await collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("Keyword")).ToListAsync();
        var version = docs.Count == 0 ? 0 : docs.Max(GetVersion);

        return new TEmojiKeywordsDifference
        {
            FromVersion = 0,
            Version = version,
            LangCode = langCode,
            Keywords = new TVector<IEmojiKeyword>(docs.Select(BuildKeyword).ToList())
        };
    }

    private static string NormalizeLangCode(string? langCode) => string.IsNullOrWhiteSpace(langCode) ? "en" : langCode.Trim().ToLowerInvariant();

    private static int GetVersion(BsonDocument doc) => doc.Contains("Version") ? doc["Version"].ToInt32() : 0;

    private static IEmojiKeyword BuildKeyword(BsonDocument doc)
    {
        return new TEmojiKeyword
        {
            Keyword = doc.Contains("Keyword") ? doc["Keyword"].AsString : string.Empty,
            Emoticons = new TVector<string>(doc.Contains("Emoticons") && doc["Emoticons"].IsBsonArray
                ? doc["Emoticons"].AsBsonArray.Select(x => x.AsString).ToList()
                : [])
        };
    }
}