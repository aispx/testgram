namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Set account self-destruction period
/// Possible errors
/// Code Type Description
/// 400 TTL_DAYS_INVALID The provided TTL is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.setAccountTTL"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetAccountTTLHandler(ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSetAccountTTL, IBool>
{
    // Range accepted by the official server. Values outside it are rejected rather than
    // silently clamped, so the client can surface TTL_DAYS_INVALID.
    private const int MinTtlDays = 30;
    private const int MaxTtlDays = 730;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSetAccountTTL obj)
    {
        var days = obj.Ttl.Days;
        if (days is < MinTtlDays or > MaxTtlDays)
        {
            RpcErrors.RpcErrors400.TtlDaysInvalid.ThrowRpcError();
        }

        await commandBus.PublishAsync(new UpdateAccountTtlCommand(
            UserId.Create(input.UserId),
            input.ToRequestInfo(),
            days));

        return new TBoolTrue();
    }
}