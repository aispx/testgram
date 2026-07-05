using MongoDB.Driver;
using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Services.Impl;

public class PrivacyAppService(
    ICacheManager<GlobalPrivacySettingsCacheItem> cacheManager,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IPrivacyService privacyService,
    IContactHelper contactHelper,
    IPrivacyHelper privacyHelper)
    : BaseAppService, IPrivacyAppService, ITransientDependency
{
    private IMongoCollection<PrivacyDocument> PrivacyCollection => mongoDatabase.GetCollection<PrivacyDocument>("privacy-readmodel");

    public async Task<IReadOnlyCollection<IPrivacyReadModel>> GetPrivacyListAsync(IReadOnlyList<long> userIds)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var docs = await PrivacyCollection.Find(Builders<PrivacyDocument>.Filter.In(p => p.UserId, userIds)).ToListAsync();
        return docs.Select(ToReadModel).ToList();
    }

    public Task<IReadOnlyCollection<IPrivacyReadModel>> GetPrivacyListAsync(long userId)
    {
        return GetPrivacyListAsync([userId]);
    }

    public Task ApplyPrivacyAsync(long selfUserId, long targetUserId, Action executeOnPrivacyNotMatch, List<PrivacyType> privacyTypes)
    {
        return ApplyPrivacyAsync(selfUserId, targetUserId, _ => executeOnPrivacyNotMatch(), privacyTypes);
    }

    public async Task ApplyPrivacyListAsync(long selfUserId, IReadOnlyList<long> targetUserIdList, Action<long> executeOnPrivacyNotMatch,
        List<PrivacyType> privacyTypes)
    {
        await ApplyPrivacyListAsync(selfUserId, targetUserIdList, (_, targetUserId) => executeOnPrivacyNotMatch(targetUserId), privacyTypes);
    }

    public async Task<IReadOnlyList<IPrivacyRule>> GetPrivacyRulesAsync(long selfUserId,
        IInputPrivacyKey key)
    {
        return await privacyService.GetPrivacyRulesAsync(selfUserId, ToPrivacyType(key));
    }

    public async Task ApplyPrivacyListAsync(long selfUserId, IReadOnlyList<long> targetUserIdList, Action<PrivacyValueType, long> executeOnPrivacyNotMatch,
        List<PrivacyType> privacyTypes)
    {
        if (targetUserIdList.Count == 0) return;
        foreach (var targetUserId in targetUserIdList)
        {
            var contactType = await GetContactTypeAsync(selfUserId, targetUserId);
            foreach (var privacyType in privacyTypes)
            {
                var privacy = await GetPrivacyReadModelAsync(targetUserId, privacyType);
                privacyHelper.ApplyPrivacy(privacy, pvt => executeOnPrivacyNotMatch(pvt, targetUserId), selfUserId, contactType);
            }
        }
    }

    public async Task SetGlobalPrivacySettingsAsync(long selfUserId, GlobalPrivacySettings globalPrivacySettings)
    {
        var cacheKey = GlobalPrivacySettingsCacheItem.GetCacheKey(selfUserId);
        var item = new GlobalPrivacySettingsCacheItem(globalPrivacySettings.ArchiveAndMuteNewNoncontactPeers,
            globalPrivacySettings.KeepArchivedUnmuted, globalPrivacySettings.KeepArchivedFolders,
            globalPrivacySettings.HideReadMarks, globalPrivacySettings.NewNoncontactPeersRequirePremium,
            globalPrivacySettings.NoncontactPeersPaidStars);
        await cacheManager.SetAsync(cacheKey, item);
    }

    public async Task<GlobalPrivacySettingsCacheItem?> GetGlobalPrivacySettingsAsync(long userId)
    {
        var cacheKey = GlobalPrivacySettingsCacheItem.GetCacheKey(userId);
        var item = await cacheManager.GetAsync(cacheKey);
        if (item != null)
        {
            return item;
        }

        var globalPrivacySettings = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(userId));
        if (globalPrivacySettings != null)
        {
            item = new GlobalPrivacySettingsCacheItem(globalPrivacySettings.ArchiveAndMuteNewNoncontactPeers,
                globalPrivacySettings.KeepArchivedUnmuted, globalPrivacySettings.KeepArchivedFolders,
                globalPrivacySettings.HideReadMarks, globalPrivacySettings.NewNoncontactPeersRequirePremium,
                globalPrivacySettings.NoncontactPeersPaidStars);
            await cacheManager.SetAsync(cacheKey, item);
        }
        return item;
    }

    public PrivacyValueData GetPrivacyValueData(IInputPrivacyRule rule)
    {
        return rule switch
        {
            TInputPrivacyValueAllowAll => new PrivacyValueData(PrivacyValueType.AllowAll),
            TInputPrivacyValueAllowContacts => new PrivacyValueData(PrivacyValueType.AllowContacts),
            TInputPrivacyValueDisallowAll => new PrivacyValueData(PrivacyValueType.DisallowAll),
            TInputPrivacyValueDisallowContacts => new PrivacyValueData(PrivacyValueType.DisallowContacts),
            TInputPrivacyValueAllowUsers r => new PrivacyValueData(PrivacyValueType.AllowUsers, SerializeInputUsers(r.Users)),
            TInputPrivacyValueDisallowUsers r => new PrivacyValueData(PrivacyValueType.DisallowUsers, SerializeInputUsers(r.Users)),
            _ => new PrivacyValueData(PrivacyValueType.Unknown)
        };
    }

    public List<PrivacyValueData> GetPrivacyValueDataList(IList<IInputPrivacyRule> rules)
    {
        return rules.Select(GetPrivacyValueData).Where(p => p.PrivacyValueType != PrivacyValueType.Unknown).ToList();
    }

    public async Task<SetPrivacyOutput> SetPrivacyAsync(RequestInfo requestInfo,
        long selfUserId,
        IInputPrivacyKey key,
        IReadOnlyList<IInputPrivacyRule> ruleList)
    {
        var privacyType = ToPrivacyType(key);
        var rules = ruleList.Select(ToPrivacyRuleEntry).ToList();
        await privacyService.SetPrivacyRulesAsync(selfUserId, privacyType, rules);
        var tlRules = await privacyService.GetPrivacyRulesAsync(selfUserId, privacyType);
        return new SetPrivacyOutput(tlRules);
    }

    public Task ApplyPrivacyAsync(long selfUserId, long targetUserId, Action<PrivacyValueType> executeOnPrivacyNotMatch, PrivacyType privacyType)
    {
        return ApplyPrivacyAsync(selfUserId, targetUserId, executeOnPrivacyNotMatch, [privacyType]);
    }

    public async Task ApplyPrivacyAsync(long selfUserId, long targetUserId, Action<PrivacyValueType> executeOnPrivacyNotMatch, List<PrivacyType> privacyTypes)
    {
        var contactType = await GetContactTypeAsync(selfUserId, targetUserId);
        foreach (var privacyType in privacyTypes)
        {
            var privacy = await GetPrivacyReadModelAsync(targetUserId, privacyType);
            privacyHelper.ApplyPrivacy(privacy, executeOnPrivacyNotMatch, selfUserId, contactType);
        }
    }

    private async Task<IPrivacyReadModel?> GetPrivacyReadModelAsync(long userId, PrivacyType privacyType)
    {
        var doc = await PrivacyCollection.Find(p => p.UserId == userId && p.PrivacyType == privacyType).FirstOrDefaultAsync();
        return doc == null ? null : ToReadModel(doc);
    }

    private async Task<ContactType> GetContactTypeAsync(long selfUserId, long targetUserId)
    {
        if (selfUserId == targetUserId)
        {
            return ContactType.Mutual;
        }

        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(selfUserId, targetUserId));
        return contactHelper.GetContactType(selfUserId, targetUserId, contactReadModels);
    }

    private static IPrivacyReadModel ToReadModel(PrivacyDocument doc)
    {
        return new PrivacyReadModelAdapter(doc.Id, doc.UserId, doc.PrivacyType,
            doc.Rules
                .Select(ToPrivacyValueData)
                .Where(p => p.PrivacyValueType != PrivacyValueType.Unknown)
                .ToList());
    }

    private static PrivacyValueData ToPrivacyValueData(PrivacyRuleEntry rule)
    {
        return rule.ValueType switch
        {
            PrivacyValueType.AllowUsers or PrivacyValueType.DisallowUsers => new PrivacyValueData(rule.ValueType, SerializeIds(rule.UserIds)),
            _ => new PrivacyValueData(rule.ValueType)
        };
    }

    private static PrivacyRuleEntry ToPrivacyRuleEntry(IInputPrivacyRule rule)
    {
        switch (rule)
        {
            case TInputPrivacyValueAllowAll:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowAll };
            case TInputPrivacyValueAllowContacts:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowContacts };
            case TInputPrivacyValueDisallowAll:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowAll };
            case TInputPrivacyValueDisallowContacts:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowContacts };
            case TInputPrivacyValueAllowUsers r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowUsers, UserIds = GetInputUserIds(r.Users) };
            case TInputPrivacyValueDisallowUsers r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowUsers, UserIds = GetInputUserIds(r.Users) };
            default:
                RpcErrors.RpcErrors400.PrivacyValueInvalid.ThrowRpcError();
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.Unknown };
        }
    }

    private static PrivacyType ToPrivacyType(IInputPrivacyKey key) => key switch
    {
        TInputPrivacyKeyStatusTimestamp => PrivacyType.StatusTimestamp,
        TInputPrivacyKeyChatInvite => PrivacyType.ChatInvite,
        TInputPrivacyKeyPhoneCall => PrivacyType.PhoneCall,
        TInputPrivacyKeyPhoneP2P => PrivacyType.PhoneP2P,
        TInputPrivacyKeyForwards => PrivacyType.Forwards,
        TInputPrivacyKeyProfilePhoto => PrivacyType.ProfilePhoto,
        TInputPrivacyKeyPhoneNumber => PrivacyType.PhoneNumber,
        TInputPrivacyKeyAddedByPhone => PrivacyType.AddedByPhone,
        TInputPrivacyKeyVoiceMessages => PrivacyType.VoiceMessages,
        TInputPrivacyKeyAbout => PrivacyType.About,
        TInputPrivacyKeyBirthday => PrivacyType.Birthday,
        TInputPrivacyKeyStarGiftsAutoSave => PrivacyType.StarGiftsAutoSave,
        TInputPrivacyKeyNoPaidMessages => PrivacyType.NoPaidMessages,
        TInputPrivacyKeySavedMusic => PrivacyType.SavedMusic,
        _ => ThrowPrivacyKeyInvalid()
    };

    private static PrivacyType ThrowPrivacyKeyInvalid()
    {
        RpcErrors.RpcErrors400.PrivacyKeyInvalid.ThrowRpcError();
        return PrivacyType.StatusTimestamp;
    }

    private static string? SerializeInputUsers(IEnumerable<IInputUser>? users)
    {
        return SerializeIds(GetInputUserIds(users));
    }

    private static List<long> GetInputUserIds(IEnumerable<IInputUser>? users)
    {
        return users?.OfType<TInputUser>().Select(p => p.UserId).Distinct().ToList() ?? [];
    }

    private static string? SerializeIds(IEnumerable<long>? ids)
    {
        var list = ids?.Distinct().ToList() ?? [];
        return list.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(list);
    }

    private sealed class PrivacyReadModelAdapter(string id, long userId, PrivacyType privacyType, IReadOnlyList<PrivacyValueData> privacyValueDataList) : IPrivacyReadModel
    {
        public string Id { get; } = id;
        public PrivacyType PrivacyType { get; } = privacyType;
        public IReadOnlyList<PrivacyValueData> PrivacyValueDataList { get; } = privacyValueDataList;
        public long UserId { get; } = userId;
    }
}
