using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Get privacy settings of current account.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getPrivacy"/> </c></para>
/// </summary>
internal sealed class GetPrivacyHandler(IPrivacyService privacyService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetPrivacy, MyTelegram.Schema.Account.IPrivacyRules>
{
    protected override async Task<IPrivacyRules> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetPrivacy obj)
    {
        var type = PrivacyMapper.ToPrivacyType(obj.Key);
        var rules = await privacyService.GetPrivacyRulesAsync(input.UserId, type);
        return new TPrivacyRules { Rules = new TVector<IPrivacyRule>(rules), Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
    }
}
