using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Obtain a list of related languages that must be used when fetching <a href="https://corefork.telegram.org/api/custom-emoji#emoji-keywords">emoji keyword lists »</a>.Usually the method will return the passed language codes (if localized) + <code>en</code> + some language codes for similar languages (if applicable).
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiKeywordsLanguages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>"If localized" is the whole point: a language named here is a language the client then asks
/// for with <c>messages.getEmojiKeywords</c>, and an empty answer is cached — Android keeps it for an
/// hour (<c>MediaDataController.fetchNewEmojiKeywords</c>) — so echoing back every requested code
/// trades a working feature for nothing. Only codes that actually have keywords are returned, plus
/// <c>en</c>, which the method is documented to always include.</para>
/// </remarks>
internal sealed class GetEmojiKeywordsLanguagesHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiKeywordsLanguages, TVector<MyTelegram.Schema.IEmojiLanguage>>
{
    private const string FallbackLangCode = "en";

    protected override async Task<TVector<MyTelegram.Schema.IEmojiLanguage>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiKeywordsLanguages obj)
    {
        var stored = await mongoDatabase.GetCollection<BsonDocument>("emoji_keywords")
            .DistinctAsync<string>("LangCode", Builders<BsonDocument>.Filter.Empty);
        var availableCodes = (await stored.ToListAsync())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var codes = new List<string>();
        if (obj.LangCodes != null)
        {
            codes.AddRange(obj.LangCodes
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(availableCodes.Contains));
        }

        codes.Add(FallbackLangCode);

        return new TVector<IEmojiLanguage>(codes.Distinct()
            .Select(x => (IEmojiLanguage)new TEmojiLanguage { LangCode = x })
            .ToList());
    }
}
