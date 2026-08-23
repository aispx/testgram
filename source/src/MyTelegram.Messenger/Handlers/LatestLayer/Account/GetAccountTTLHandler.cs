namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get days to live of account
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getAccountTTL"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAccountTTLHandler(IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetAccountTTL, MyTelegram.Schema.IAccountDaysTTL>
{
    /// <summary>Matches the value <c>UserAggregate</c> stores when an account is created.</summary>
    private const int DefaultAccountTtlDays = 365;

    protected override async Task<MyTelegram.Schema.IAccountDaysTTL> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetAccountTTL obj)
    {
        var user = await userAppService.GetAsync((long?)input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Accounts created before the TTL was tracked have no value stored; the server default is
        // what account.setAccountTTL would otherwise be compared against.
        var days = user!.AccountTtl > 0 ? user.AccountTtl : DefaultAccountTtlDays;

        return new TAccountDaysTTL
        {
            Days = days
        };
    }
}