using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Privacy;

/// <summary>
/// Single source of truth for translating between TL privacy constructors and the internal
/// <see cref="PrivacyType"/> / <see cref="PrivacyValueType"/> enums.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
/// <remarks>
/// These mappings used to be copy-pasted across <c>GetPrivacyHandler</c>,
/// <c>SetPrivacyHandler</c> and <c>PrivacyAppService</c>, which is how the newer rule types
/// ended up supported in some copies and rejected in others.
/// </remarks>
public static class PrivacyMapper
{
    public static PrivacyType ToPrivacyType(IInputPrivacyKey key) => key switch
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

    public static IPrivacyKey ToPrivacyKey(PrivacyType type) => type switch
    {
        PrivacyType.StatusTimestamp => new TPrivacyKeyStatusTimestamp(),
        PrivacyType.ChatInvite => new TPrivacyKeyChatInvite(),
        PrivacyType.PhoneCall => new TPrivacyKeyPhoneCall(),
        PrivacyType.PhoneP2P => new TPrivacyKeyPhoneP2P(),
        PrivacyType.Forwards => new TPrivacyKeyForwards(),
        PrivacyType.ProfilePhoto => new TPrivacyKeyProfilePhoto(),
        PrivacyType.PhoneNumber => new TPrivacyKeyPhoneNumber(),
        PrivacyType.AddedByPhone => new TPrivacyKeyAddedByPhone(),
        PrivacyType.VoiceMessages => new TPrivacyKeyVoiceMessages(),
        PrivacyType.About => new TPrivacyKeyAbout(),
        PrivacyType.Birthday => new TPrivacyKeyBirthday(),
        PrivacyType.StarGiftsAutoSave => new TPrivacyKeyStarGiftsAutoSave(),
        PrivacyType.NoPaidMessages => new TPrivacyKeyNoPaidMessages(),
        PrivacyType.SavedMusic => new TPrivacyKeySavedMusic(),
        _ => new TPrivacyKeyStatusTimestamp()
    };

    public static PrivacyRuleEntry ToPrivacyRuleEntry(IInputPrivacyRule rule)
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
            case TInputPrivacyValueAllowPremium:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowPremium };
            case TInputPrivacyValueAllowCloseFriends:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowCloseFriends };
            case TInputPrivacyValueAllowBots:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowBots };
            case TInputPrivacyValueDisallowBots:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowBots };
            case TInputPrivacyValueAllowUsers r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowUsers, UserIds = GetInputUserIds(r.Users) };
            case TInputPrivacyValueDisallowUsers r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowUsers, UserIds = GetInputUserIds(r.Users) };
            case TInputPrivacyValueAllowChatParticipants r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowChatParticipants, ChatIds = GetChatIds(r.Chats) };
            case TInputPrivacyValueDisallowChatParticipants r:
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowChatParticipants, ChatIds = GetChatIds(r.Chats) };
            default:
                RpcErrors.RpcErrors400.PrivacyValueInvalid.ThrowRpcError();
                return new PrivacyRuleEntry { ValueType = PrivacyValueType.Unknown };
        }
    }

    public static IPrivacyRule ToTlRule(PrivacyRuleEntry rule) => rule.ValueType switch
    {
        PrivacyValueType.AllowAll => new TPrivacyValueAllowAll(),
        PrivacyValueType.AllowContacts => new TPrivacyValueAllowContacts(),
        PrivacyValueType.DisallowAll => new TPrivacyValueDisallowAll(),
        PrivacyValueType.DisallowContacts => new TPrivacyValueDisallowContacts(),
        PrivacyValueType.AllowPremium => new TPrivacyValueAllowPremium(),
        PrivacyValueType.AllowCloseFriends => new TPrivacyValueAllowCloseFriends(),
        PrivacyValueType.AllowBots => new TPrivacyValueAllowBots(),
        PrivacyValueType.DisallowBots => new TPrivacyValueDisallowBots(),
        PrivacyValueType.AllowUsers => new TPrivacyValueAllowUsers { Users = new TVector<long>(rule.UserIds ?? []) },
        PrivacyValueType.DisallowUsers => new TPrivacyValueDisallowUsers { Users = new TVector<long>(rule.UserIds ?? []) },
        PrivacyValueType.AllowChatParticipants => new TPrivacyValueAllowChatParticipants { Chats = new TVector<long>(rule.ChatIds ?? []) },
        PrivacyValueType.DisallowChatParticipants => new TPrivacyValueDisallowChatParticipants { Chats = new TVector<long>(rule.ChatIds ?? []) },
        _ => new TPrivacyValueDisallowAll()
    };

    public static List<long> GetInputUserIds(IEnumerable<IInputUser>? users)
    {
        return users?.OfType<TInputUser>().Select(p => p.UserId).Distinct().ToList() ?? [];
    }

    private static List<long> GetChatIds(IEnumerable<long>? chats)
    {
        return chats?.Distinct().ToList() ?? [];
    }

    private static PrivacyType ThrowPrivacyKeyInvalid()
    {
        RpcErrors.RpcErrors400.PrivacyKeyInvalid.ThrowRpcError();
        return PrivacyType.StatusTimestamp;
    }
}
