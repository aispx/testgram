using MongoDB.Driver;
using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.Messenger.Services.Stories;

namespace MyTelegram.Messenger.Services.Impl;

public class PrivacyAppService(
    ICacheManager<GlobalPrivacySettingsCacheItem> cacheManager,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase,
    IPrivacyService privacyService,
    IContactHelper contactHelper,
    IUserAppService userAppService,
    IPrivacyHelper privacyHelper)
    : BaseAppService, IPrivacyAppService, ITransientDependency
{
    private IMongoCollection<PrivacyDocument> PrivacyCollection => mongoDatabase.GetCollection<PrivacyDocument>("privacy-readmodel");
    private IMongoCollection<CloseFriendDocument> CloseFriendCollection => mongoDatabase.GetCollection<CloseFriendDocument>("close_friends");

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
        var viewerContext = await BuildViewerContextAsync(selfUserId, targetUserIdList);
        foreach (var targetUserId in targetUserIdList)
        {
            var contactType = await GetContactTypeAsync(selfUserId, targetUserId);
            foreach (var privacyType in privacyTypes)
            {
                var privacy = await GetPrivacyReadModelAsync(targetUserId, privacyType);
                privacyHelper.ApplyPrivacy(privacy, pvt => executeOnPrivacyNotMatch(pvt, targetUserId), selfUserId,
                    contactType, viewerContext(targetUserId));
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
        // Routed through the shared mapper so newly supported rule kinds (premium, bots,
        // close friends, chat participants) do not silently degrade to Unknown here.
        return rule switch
        {
            TInputPrivacyValueAllowAll => new PrivacyValueData(PrivacyValueType.AllowAll),
            TInputPrivacyValueAllowContacts => new PrivacyValueData(PrivacyValueType.AllowContacts),
            TInputPrivacyValueDisallowAll => new PrivacyValueData(PrivacyValueType.DisallowAll),
            TInputPrivacyValueDisallowContacts => new PrivacyValueData(PrivacyValueType.DisallowContacts),
            TInputPrivacyValueAllowUsers r => new PrivacyValueData(PrivacyValueType.AllowUsers, SerializeInputUsers(r.Users)),
            TInputPrivacyValueDisallowUsers r => new PrivacyValueData(PrivacyValueType.DisallowUsers, SerializeInputUsers(r.Users)),
            TInputPrivacyValueAllowPremium => new PrivacyValueData(PrivacyValueType.AllowPremium),
            TInputPrivacyValueAllowCloseFriends => new PrivacyValueData(PrivacyValueType.AllowCloseFriends),
            TInputPrivacyValueAllowBots => new PrivacyValueData(PrivacyValueType.AllowBots),
            TInputPrivacyValueDisallowBots => new PrivacyValueData(PrivacyValueType.DisallowBots),
            TInputPrivacyValueAllowChatParticipants r => new PrivacyValueData(PrivacyValueType.AllowChatParticipants, SerializeIds(r.Chats)),
            TInputPrivacyValueDisallowChatParticipants r => new PrivacyValueData(PrivacyValueType.DisallowChatParticipants, SerializeIds(r.Chats)),
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
        var viewerContext = await BuildViewerContextAsync(selfUserId, [targetUserId]);
        foreach (var privacyType in privacyTypes)
        {
            var privacy = await GetPrivacyReadModelAsync(targetUserId, privacyType);
            privacyHelper.ApplyPrivacy(privacy, executeOnPrivacyNotMatch, selfUserId, contactType, viewerContext(targetUserId));
        }
    }

    /// <summary>
    /// Collects the viewer facts the newer privacy rules need, once per call rather than per
    /// rule, and returns a lookup keyed by target user (close-friend status is per target).
    /// </summary>
    private async Task<Func<long, PrivacyViewerContext>> BuildViewerContextAsync(long selfUserId, IReadOnlyList<long> targetUserIdList)
    {
        var viewer = await userAppService.GetAsync(selfUserId);
        var joinedChatIds = await queryProcessor.ProcessAsync(new GetAllJoinedChannelIdListQuery(selfUserId));

        var closeFriendDocs = await CloseFriendCollection
            .Find(Builders<CloseFriendDocument>.Filter.In(p => p.SelfUserId, targetUserIdList))
            .ToListAsync();
        // A target counts the viewer as a close friend only if the viewer is on that target's list.
        var targetsWithViewerAsCloseFriend = closeFriendDocs
            .Where(p => p.UserIds.Contains(selfUserId))
            .Select(p => p.SelfUserId)
            .ToHashSet();

        var chatIds = joinedChatIds.ToHashSet();
        var isPremium = viewer?.Premium == true;
        var isBot = viewer?.Bot == true;

        return targetUserId => new PrivacyViewerContext(
            isPremium,
            isBot,
            targetsWithViewerAsCloseFriend.Contains(targetUserId),
            chatIds);
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
            // Chat-participant rules carry chat ids in their own field; serialising UserIds here
            // would hand the evaluator an empty list and the rule would never match.
            PrivacyValueType.AllowChatParticipants or PrivacyValueType.DisallowChatParticipants => new PrivacyValueData(rule.ValueType, SerializeIds(rule.ChatIds)),
            _ => new PrivacyValueData(rule.ValueType)
        };
    }

    private static PrivacyRuleEntry ToPrivacyRuleEntry(IInputPrivacyRule rule)
    {
        return PrivacyMapper.ToPrivacyRuleEntry(rule);
    }

    private static PrivacyType ToPrivacyType(IInputPrivacyKey key)
    {
        return PrivacyMapper.ToPrivacyType(key);
    }

    private static string? SerializeInputUsers(IEnumerable<IInputUser>? users)
    {
        return SerializeIds(PrivacyMapper.GetInputUserIds(users));
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
