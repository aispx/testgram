using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class SetPrivacyHandler(
    IPrivacyService privacyService,
    IObjectMessageSender messageSender)
    : RpcResultObjectHandler<RequestSetPrivacy, IPrivacyRules>
{
    protected override async Task<IPrivacyRules> HandleCoreAsync(IRequestInput input, RequestSetPrivacy obj)
    {
        var type = ToPrivacyType(obj.Key);
        var rules = obj.Rules.Select(ToEntry).ToList();
        await privacyService.SetPrivacyRulesAsync(input.UserId, type, rules);
        var tlRules = await privacyService.GetPrivacyRulesAsync(input.UserId, type);

        var privacyKey = ToPrivacyKey(type);
        var updatePrivacy = new TUpdatePrivacy
        {
            Key = privacyKey,
            Rules = new TVector<IPrivacyRule>(tlRules)
        };
        var updates = new TUpdates
        {
            Updates = [updatePrivacy],
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
        await messageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            updates,
            excludeAuthKeyId: input.AuthKeyId
        );

        return new TPrivacyRules { Rules = new TVector<IPrivacyRule>(tlRules), Chats = [], Users = [] };
    }

    private static PrivacyRuleEntry ToEntry(IInputPrivacyRule rule) => rule switch
    {
        TInputPrivacyValueAllowAll => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowAll },
        TInputPrivacyValueAllowContacts => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowContacts },
        TInputPrivacyValueDisallowAll => new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowAll },
        TInputPrivacyValueDisallowContacts => new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowContacts },
        TInputPrivacyValueAllowUsers r => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowUsers, UserIds = r.Users?.OfType<TInputUser>().Select(u => u.UserId).ToList() },
        TInputPrivacyValueDisallowUsers r => new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowUsers, UserIds = r.Users?.OfType<TInputUser>().Select(u => u.UserId).ToList() },
        TInputPrivacyValueAllowChatParticipants r => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowChatParticipants, ChatIds = r.Chats?.ToList() },
        TInputPrivacyValueDisallowChatParticipants r => new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowChatParticipants, ChatIds = r.Chats?.ToList() },
        TInputPrivacyValueAllowCloseFriends => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowCloseFriends },
        TInputPrivacyValueAllowBots => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowBots },
        TInputPrivacyValueDisallowBots => new PrivacyRuleEntry { ValueType = PrivacyValueType.DisallowBots },
        TInputPrivacyValueAllowPremium => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowPremium },
        _ => new PrivacyRuleEntry { ValueType = PrivacyValueType.AllowAll }
    };

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
        _ => PrivacyType.StatusTimestamp
    };

    private static IPrivacyKey ToPrivacyKey(PrivacyType type) => type switch
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
}
