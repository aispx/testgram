using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetPrivacyHandler(IPrivacyService privacyService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPrivacy, MyTelegram.Schema.Account.IPrivacyRules>
{
    protected override async Task<IPrivacyRules> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPrivacy obj)
    {
        var type = ToPrivacyType(obj.Key);
        var rules = await privacyService.GetPrivacyRulesAsync(input.UserId, type);
        return new TPrivacyRules { Rules = new TVector<IPrivacyRule>(rules), Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
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
        _ => PrivacyType.StatusTimestamp
    };
}
