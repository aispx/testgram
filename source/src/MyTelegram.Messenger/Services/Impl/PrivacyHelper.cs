using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Services.Impl;

public class PrivacyHelper : IPrivacyHelper, ITransientDependency
{
    public void ApplyPrivacy(IPrivacyReadModel? privacyReadModel,
        Action executeOnPrivacyNotMatch,
        long selfUserId,
        bool isContact)
    {
        ApplyPrivacy(privacyReadModel, _ => executeOnPrivacyNotMatch(), selfUserId,
            isContact ? ContactType.ContactOfTargetUser : ContactType.None);
    }

    public void ApplyPrivacy(IPrivacyReadModel? privacyReadModel, Action<PrivacyValueType> executeOnPrivacyNotMatch, long selfUserId,
        ContactType contactType)
    {
        ApplyPrivacy(privacyReadModel, executeOnPrivacyNotMatch, selfUserId, contactType, PrivacyViewerContext.Unknown);
    }

    public void ApplyPrivacy(IPrivacyReadModel? privacyReadModel, Action<PrivacyValueType> executeOnPrivacyNotMatch, long selfUserId,
        ContactType contactType, PrivacyViewerContext viewerContext)
    {
        if (!IsAllowedByPrivacy(selfUserId, privacyReadModel, contactType, viewerContext))
        {
            executeOnPrivacyNotMatch(GetMatchedPrivacyValueType(selfUserId, privacyReadModel, contactType, viewerContext));
        }
    }

    public bool IsAllowedByPrivacy(long selfUserId, IPrivacyReadModel? privacyReadModel, ContactType contactType)
    {
        return IsAllowedByPrivacy(selfUserId, privacyReadModel, contactType, PrivacyViewerContext.Unknown);
    }

    public bool IsAllowedByPrivacy(long selfUserId, IPrivacyReadModel? privacyReadModel, ContactType contactType,
        PrivacyViewerContext viewerContext)
    {
        if (privacyReadModel == null || privacyReadModel.PrivacyValueDataList.Count == 0)
        {
            return true;
        }

        var isContactOfTarget = contactType is ContactType.Mutual or ContactType.ContactOfTargetUser;
        var rules = privacyReadModel.PrivacyValueDataList;

        if (rules.Any(p => !IsSupportedByEvaluator(p.PrivacyValueType)))
        {
            return false;
        }

        // Deny beats allow at the same specificity, and more specific rules are considered
        // first: explicit users, then category (bots / shared chats / close friends /
        // contacts / premium), then everybody.
        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowUsers && ContainsId(p, selfUserId)))
        {
            return false;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowUsers && ContainsId(p, selfUserId)))
        {
            return true;
        }

        if (viewerContext.IsBot && rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowBots))
        {
            return false;
        }

        if (viewerContext.IsBot && rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowBots))
        {
            return true;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowChatParticipants
                           && SharesChat(p, viewerContext)))
        {
            return false;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowChatParticipants
                           && SharesChat(p, viewerContext)))
        {
            return true;
        }

        if (viewerContext.IsCloseFriend && rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowCloseFriends))
        {
            return true;
        }

        if (isContactOfTarget && rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowContacts))
        {
            return false;
        }

        if (isContactOfTarget && rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowContacts))
        {
            return true;
        }

        if (viewerContext.IsPremium && rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowPremium))
        {
            return true;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowAll))
        {
            return false;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowAll))
        {
            return true;
        }

        // Only narrowing allow rules are left and the viewer matched none of them.
        return !rules.Any(p => p.PrivacyValueType is PrivacyValueType.AllowContacts
            or PrivacyValueType.AllowUsers
            or PrivacyValueType.AllowPremium
            or PrivacyValueType.AllowCloseFriends
            or PrivacyValueType.AllowBots
            or PrivacyValueType.AllowChatParticipants);
    }

    private static PrivacyValueType GetMatchedPrivacyValueType(long selfUserId, IPrivacyReadModel? privacyReadModel,
        ContactType contactType, PrivacyViewerContext viewerContext)
    {
        if (privacyReadModel == null)
        {
            return PrivacyValueType.DisallowAll;
        }

        var isContactOfTarget = contactType is ContactType.Mutual or ContactType.ContactOfTargetUser;
        var unsupportedRule = privacyReadModel.PrivacyValueDataList.FirstOrDefault(p => !IsSupportedByEvaluator(p.PrivacyValueType));
        if (unsupportedRule != null)
        {
            return unsupportedRule.PrivacyValueType;
        }

        return privacyReadModel.PrivacyValueDataList.FirstOrDefault(p =>
            p.PrivacyValueType == PrivacyValueType.DisallowAll ||
            (p.PrivacyValueType == PrivacyValueType.DisallowUsers && ContainsId(p, selfUserId)) ||
            (p.PrivacyValueType == PrivacyValueType.DisallowBots && viewerContext.IsBot) ||
            (p.PrivacyValueType == PrivacyValueType.DisallowChatParticipants && SharesChat(p, viewerContext)) ||
            (p.PrivacyValueType == PrivacyValueType.DisallowContacts && isContactOfTarget))?.PrivacyValueType
            ?? PrivacyValueType.DisallowAll;
    }

    private static bool IsSupportedByEvaluator(PrivacyValueType valueType)
    {
        return PrivacyRuleEntry.IsSupportedByEvaluatorValueType(valueType);
    }

    private static bool SharesChat(PrivacyValueData data, PrivacyViewerContext viewerContext)
    {
        if (viewerContext.ChatIds.Count == 0)
        {
            return false;
        }

        return DeserializeIds(data).Any(viewerContext.ChatIds.Contains);
    }

    private static bool ContainsId(PrivacyValueData data, long id)
    {
        return DeserializeIds(data).Contains(id);
    }

    private static List<long> DeserializeIds(PrivacyValueData data)
    {
        if (string.IsNullOrWhiteSpace(data.JsonData))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<long>>(data.JsonData) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
