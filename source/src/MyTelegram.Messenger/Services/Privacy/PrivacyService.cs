using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Privacy;

public interface IPrivacyService
{
    Task<List<IPrivacyRule>> GetPrivacyRulesAsync(long userId, PrivacyType type);
    Task SetPrivacyRulesAsync(long userId, PrivacyType type, List<PrivacyRuleEntry> rules);
}

public class PrivacyService(IMongoDatabase mongoDatabase) : IPrivacyService, ISingletonDependency
{
    private const string CollectionName = "privacy-readmodel";
    private IMongoCollection<PrivacyDocument> Collection => mongoDatabase.GetCollection<PrivacyDocument>(CollectionName);

    public async Task<List<IPrivacyRule>> GetPrivacyRulesAsync(long userId, PrivacyType type)
    {
        var doc = await Collection.Find(p => p.UserId == userId && p.PrivacyType == type).FirstOrDefaultAsync();
        if (doc == null) return [GetDefaultRule(type)];
        var rules = doc.Rules.Where(p => p.IsSupportedByEvaluator).Select(PrivacyMapper.ToTlRule).ToList();
        return rules.Count == 0 ? [GetDefaultRule(type)] : rules;
    }

    /// <summary>
    /// Rule reported when the user never configured this key. Returning an empty rule list
    /// instead leaves clients guessing, and for the phone number the documented default is
    /// stricter than "everybody".
    /// See https://corefork.telegram.org/api/privacy
    /// </summary>
    public static IPrivacyRule GetDefaultRule(PrivacyType type) => type switch
    {
        PrivacyType.PhoneNumber => new TPrivacyValueAllowContacts(),
        _ => new TPrivacyValueAllowAll()
    };

    public async Task SetPrivacyRulesAsync(long userId, PrivacyType type, List<PrivacyRuleEntry> rules)
    {
        var doc = new PrivacyDocument
        {
            Id = $"{userId}:{(int)type}",
            UserId = userId,
            PrivacyType = type,
            Rules = rules.Where(p => p.IsSupportedByEvaluator).ToList()
        };
        await Collection.ReplaceOneAsync(
            p => p.UserId == userId && p.PrivacyType == type,
            doc,
            new ReplaceOptions { IsUpsert = true });
    }
}
