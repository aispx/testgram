using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get changed <a href="https://corefork.telegram.org/api/custom-emoji#emoji-keywords">emoji keywords »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiKeywordsDifference"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetEmojiKeywordsDifferenceHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiKeywordsDifference, MyTelegram.Schema.IEmojiKeywordsDifference>
{
    protected override async Task<MyTelegram.Schema.IEmojiKeywordsDifference> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiKeywordsDifference obj)
    {
        var langCode = NormalizeLangCode(obj.LangCode);
        var collection = mongoDatabase.GetCollection<BsonDocument>("emoji_keywords");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("LangCode", langCode),
            Builders<BsonDocument>.Filter.Gt("Version", obj.FromVersion));
        var docs = await collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("Version").Ascending("Keyword")).ToListAsync();
        var version = docs.Count == 0 ? obj.FromVersion : docs.Max(GetVersion);

        return new TEmojiKeywordsDifference
        {
            LangCode = langCode,
            FromVersion = obj.FromVersion,
            Version = version,
            Keywords = new TVector<IEmojiKeyword>(docs.Select(BuildKeyword).ToList())
        };
    }

    private static string NormalizeLangCode(string? langCode) => string.IsNullOrWhiteSpace(langCode) ? "en" : langCode.Trim().ToLowerInvariant();

    private static int GetVersion(BsonDocument doc) => doc.Contains("Version") ? doc["Version"].ToInt32() : 0;

    private static IEmojiKeyword BuildKeyword(BsonDocument doc)
    {
        var isDeleted = doc.Contains("Deleted") && doc["Deleted"].ToBoolean();
        var emoticons = new TVector<string>(doc.Contains("Emoticons") && doc["Emoticons"].IsBsonArray
            ? doc["Emoticons"].AsBsonArray.Select(x => x.AsString).ToList()
            : []);
        return isDeleted
            ? new TEmojiKeywordDeleted
            {
                Keyword = doc.Contains("Keyword") ? doc["Keyword"].AsString : string.Empty,
                Emoticons = emoticons
            }
            : new TEmojiKeyword
            {
                Keyword = doc.Contains("Keyword") ? doc["Keyword"].AsString : string.Empty,
                Emoticons = emoticons
            };
    }
}