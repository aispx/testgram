namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Obtain a list of related languages that must be used when fetching <a href="https://corefork.telegram.org/api/custom-emoji#emoji-keywords">emoji keyword lists »</a>.Usually the method will return the passed language codes (if localized) + <code>en</code> + some language codes for similar languages (if applicable).
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiKeywordsLanguages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetEmojiKeywordsLanguagesHandler : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiKeywordsLanguages, TVector<MyTelegram.Schema.IEmojiLanguage>>
{
    protected override Task<TVector<MyTelegram.Schema.IEmojiLanguage>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiKeywordsLanguages obj)
    {
        var codes = new List<string>();
        if (obj.LangCodes != null)
        {
            codes.AddRange(obj.LangCodes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()));
        }

        if (!codes.Contains("en"))
        {
            codes.Add("en");
        }

        var result = new TVector<IEmojiLanguage>(codes.Distinct().Select(x => (IEmojiLanguage)new TEmojiLanguage { LangCode = x }).ToList());
        return Task.FromResult(result);
    }
}