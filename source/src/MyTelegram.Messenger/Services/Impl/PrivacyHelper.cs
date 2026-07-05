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
        if (!IsAllowedByPrivacy(selfUserId, privacyReadModel, contactType))
        {
            executeOnPrivacyNotMatch(GetMatchedPrivacyValueType(selfUserId, privacyReadModel, contactType));
        }
    }

    public bool IsAllowedByPrivacy(long selfUserId, IPrivacyReadModel? privacyReadModel, ContactType contactType)
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

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowUsers && ContainsId(p, selfUserId)))
        {
            return false;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowUsers && ContainsId(p, selfUserId)))
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

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.DisallowAll))
        {
            return false;
        }

        if (rules.Any(p => p.PrivacyValueType == PrivacyValueType.AllowAll))
        {
            return true;
        }

        return !rules.Any(p => p.PrivacyValueType is PrivacyValueType.AllowContacts
            or PrivacyValueType.AllowUsers);
    }

    private static PrivacyValueType GetMatchedPrivacyValueType(long selfUserId, IPrivacyReadModel? privacyReadModel, ContactType contactType)
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
            (p.PrivacyValueType == PrivacyValueType.DisallowContacts && isContactOfTarget))?.PrivacyValueType
            ?? PrivacyValueType.DisallowAll;
    }

    private static bool IsSupportedByEvaluator(PrivacyValueType valueType)
    {
        return valueType is PrivacyValueType.AllowContacts
            or PrivacyValueType.AllowAll
            or PrivacyValueType.AllowUsers
            or PrivacyValueType.DisallowContacts
            or PrivacyValueType.DisallowAll
            or PrivacyValueType.DisallowUsers;
    }

    private static bool ContainsId(PrivacyValueData data, long id)
    {
        if (string.IsNullOrWhiteSpace(data.JsonData))
        {
            return false;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<long>>(data.JsonData)?.Contains(id) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
