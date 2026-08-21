namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Updates online user status.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateStatus"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateStatusHandler(
    IUserStatusCacheAppService userStatusAppService,
    IUserStatusUpdateNotifier userStatusUpdateNotifier) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateStatus, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateStatus obj)
    {
        // Clients ping this method roughly every minute while in the foreground, so the update only
        // goes out when the status other users see actually changed.
        // See https://corefork.telegram.org/api/peers#handling-updates
        if (userStatusAppService.UpdateStatus(input.UserId, !obj.Offline))
        {
            await userStatusUpdateNotifier.NotifyStatusChangedAsync(input, input.UserId);
        }

        return new TBoolTrue();
    }
}
