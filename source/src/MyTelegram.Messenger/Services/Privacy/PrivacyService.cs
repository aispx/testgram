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
        if (doc == null) return [];
        return doc.Rules.Where(p => p.IsSupportedByEvaluator).Select(ToTlRule).ToList();
    }

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

    private static IPrivacyRule ToTlRule(PrivacyRuleEntry r) => r.ValueType switch
    {
        PrivacyValueType.AllowAll => new TPrivacyValueAllowAll(),
        PrivacyValueType.AllowContacts => new TPrivacyValueAllowContacts(),
        PrivacyValueType.DisallowAll => new TPrivacyValueDisallowAll(),
        PrivacyValueType.DisallowContacts => new TPrivacyValueDisallowContacts(),
        PrivacyValueType.AllowUsers => new TPrivacyValueAllowUsers { Users = new TVector<long>(r.UserIds ?? []) },
        PrivacyValueType.DisallowUsers => new TPrivacyValueDisallowUsers { Users = new TVector<long>(r.UserIds ?? []) },
        _ => new TPrivacyValueDisallowAll()
    };
}
