using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Change privacy settings of current account.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setPrivacy"/> </c></para>
/// </summary>
internal sealed class SetPrivacyHandler(
    IPrivacyService privacyService,
    IObjectMessageSender messageSender)
    : RpcResultObjectHandler<RequestSetPrivacy, IPrivacyRules>
{
    protected override async Task<IPrivacyRules> HandleCoreAsync(IRequestInput input, RequestSetPrivacy obj)
    {
        var type = PrivacyMapper.ToPrivacyType(obj.Key);
        var rules = obj.Rules.Select(PrivacyMapper.ToPrivacyRuleEntry).ToList();
        await privacyService.SetPrivacyRulesAsync(input.UserId, type, rules);
        var tlRules = await privacyService.GetPrivacyRulesAsync(input.UserId, type);

        var privacyKey = PrivacyMapper.ToPrivacyKey(type);
        var updatePrivacy = new TUpdatePrivacy
        {
            Key = privacyKey,
            Rules = new TVector<IPrivacyRule>(tlRules)
        };
        var updates = new TUpdates
        {
            Updates = [updatePrivacy],
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
        await messageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            updates,
            excludeAuthKeyId: input.AuthKeyId
        );

        return new TPrivacyRules { Rules = new TVector<IPrivacyRule>(tlRules), Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
    }
}
